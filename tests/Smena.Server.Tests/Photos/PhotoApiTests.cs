using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Smena.Core.Accounting;
using Smena.Core.Photos;
using Smena.Core.Shifts;
using Smena.Server.Accounting;
using Smena.Server.Cashier;
using Smena.Server.Database;
using Smena.Server.Employees;
using Smena.Server.Notifications;
using Smena.Server.Photos;
using Smena.Server.Root;

namespace Smena.Server.Tests.Photos;

/// <summary>Фото к смене: касса создаёт сессию, телефон загружает снимки, смена их забирает.</summary>
public sealed class PhotoApiTests(SmenaApp app) : IAsyncLifetime
{
    private const int OpeningSafe = 50000;
    private const long AdminChat = -1001;
    private const int ReportsThread = 11;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private Guid _ivan;
    private int _seed;

    public async ValueTask InitializeAsync()
    {
        await app.ResetLedgerAsync();

        _ivan = await app.QueryDatabaseAsync(async db =>
        {
            var employee = Employee.Create(1001, "Иван", 200);
            db.Employees.Add(employee);
            await db.SaveChangesAsync(Ct);
            return employee.Id;
        });

        await app.InScopeAsync(async services =>
        {
            var ledger = services.GetRequiredService<Ledger>();
            await using var transaction = await ledger.BeginAsync(Ct);
            ledger.Post(new OperationDraft(
                OperationType.Opening,
                DateTimeOffset.UtcNow,
                new Actor(ActorKind.Root, "root"),
                [TransactionDraft.Create(OpeningSafe, Leg.Increase(SystemAccounts.Safe), Leg.Increase(SystemAccounts.Opening))]));
            await services.GetRequiredService<SmenaDbContext>().SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            return 0;
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Сессия ----

    [Fact]
    public async Task Session_requires_the_cashier_token()
    {
        using var anonymous = app.CreateClient();
        using var root = await app.CreateRootClientAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Relative("/api/photo-sessions"), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await root.PostAsync(Relative("/api/photo-sessions"), null, Ct)).StatusCode);
    }

    [Fact]
    public async Task New_session_gives_a_link_for_the_phone()
    {
        using var cashier = app.CreateCashierClient();

        var session = await CreateSessionAsync(cashier);

        Assert.StartsWith(SmenaApp.PublicBaseUrl + "/u/", session.UploadUrl, StringComparison.Ordinal);
        Assert.Equal(3, session.MinPhotos);
        Assert.Equal(PhotoRules.MaxPerSession, session.MaxPhotos);
        Assert.Empty(session.Photos);

        // Секрет достаточно длинный, чтобы его нельзя было подобрать.
        Assert.True(Token(session).Length >= 32);
    }

    [Fact]
    public async Task Upload_page_is_served_for_any_token()
    {
        using var client = app.CreateClient();

        using var response = await client.GetAsync(Relative("/u/whatever"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<html", await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Загрузка ----

    [Fact]
    public async Task Uploaded_photo_is_shrunk_reencoded_and_listed()
    {
        using var cashier = app.CreateCashierClient();
        using var phone = app.CreateClient();
        var session = await CreateSessionAsync(cashier);
        var takenLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(-20), Moscow);

        using var response = await UploadAsync(phone, session, Picture(width: 4000, height: 3000, takenLocal: takenLocal.DateTime));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var uploaded = await ReadAsync<PhotoResponse>(response);
        Assert.False(uploaded.IsStale);
        Assert.NotNull(uploaded.TakenAt);
        Assert.InRange((takenLocal - uploaded.TakenAt.Value).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // Касса и телефон видят одно и то же.
        Assert.Equal([uploaded.Id], (await GetAsync<PhotoSessionResponse>(cashier, $"/api/photo-sessions/{session.Id}")).Photos.Select(p => p.Id));
        Assert.Equal([uploaded.Id], (await GetAsync<UploadStatusResponse>(phone, $"/api/upload/{Token(session)}")).Photos.Select(p => p.Id));

        // На диске — уменьшенный JPEG без метаданных исходника.
        var stored = await app.QueryDatabaseAsync(db => db.Photos.AsNoTracking().SingleAsync(Ct));
        Assert.Equal((1600, 1200), (stored.Width, stored.Height));

        var file = await File.ReadAllBytesAsync(Path.Combine(app.PhotoDirectory, stored.Path), Ct);
        Assert.Equal(stored.Bytes, file.Length);
        Assert.Equal(PhotoFormat.Jpeg, PhotoFormats.Detect(file));
        using var image = new MagickImage(file);
        Assert.Equal((1600u, 1200u), (image.Width, image.Height));
        Assert.Null(image.GetExifProfile());

        // Телефон показывает снимок по той же ссылке.
        using var picture = await phone.GetAsync(Relative($"/api/upload/{Token(session)}/photos/{uploaded.Id}"), Ct);
        Assert.Equal("image/jpeg", picture.Content.Headers.ContentType?.MediaType);
        Assert.Equal(file, await picture.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory]
    [InlineData(800, 600, 800, 600)]       // маленький снимок не растягивается
    [InlineData(1600, 1200, 1600, 1200)]   // ровно по границе
    [InlineData(3000, 4000, 1200, 1600)]   // портрет
    [InlineData(8000, 6000, 1600, 1200)]   // большой: уменьшается ещё при чтении
    [InlineData(6400, 1000, 1600, 250)]    // панорама
    public async Task Photo_is_fitted_into_the_size_limit(int width, int height, int storedWidth, int storedHeight)
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);

        (await UploadAsync(cashier, session, Picture((uint)width, (uint)height))).EnsureSuccessStatusCode();

        var stored = await app.QueryDatabaseAsync(db => db.Photos.AsNoTracking().SingleAsync(Ct));
        Assert.Equal((storedWidth, storedHeight), (stored.Width, stored.Height));
    }

    [Fact]
    public async Task Png_is_accepted_and_stored_as_jpeg()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);

        (await UploadAsync(cashier, session, Picture(format: MagickFormat.Png))).EnsureSuccessStatusCode();

        var stored = await app.QueryDatabaseAsync(db => db.Photos.AsNoTracking().SingleAsync(Ct));
        var file = await File.ReadAllBytesAsync(Path.Combine(app.PhotoDirectory, stored.Path), Ct);
        Assert.Equal(PhotoFormat.Jpeg, PhotoFormats.Detect(file));
    }

    [Fact]
    public async Task Photo_without_time_or_too_old_is_accepted_but_flagged()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        var sixHoursAgo = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddHours(-6), Moscow).DateTime;
        var fourHoursAgo = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddHours(-4), Moscow).DateTime;

        var noTime = await ReadAsync<PhotoResponse>(await UploadAsync(cashier, session, Picture()));
        var old = await ReadAsync<PhotoResponse>(await UploadAsync(cashier, session, Picture(takenLocal: sixHoursAgo)));
        var fresh = await ReadAsync<PhotoResponse>(await UploadAsync(cashier, session, Picture(takenLocal: fourHoursAgo)));

        Assert.True(noTime.IsStale);
        Assert.Null(noTime.TakenAt);
        Assert.True(old.IsStale);
        Assert.False(fresh.IsStale);
    }

    [Fact]
    public async Task Offset_inside_the_file_is_used_for_the_time()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);

        // Телефон в другом поясе (+07:00): по времени клуба снимок был бы «из будущего».
        var taken = DateTimeOffset.UtcNow.AddMinutes(-30).ToOffset(TimeSpan.FromHours(7));

        var uploaded = await ReadAsync<PhotoResponse>(
            await UploadAsync(cashier, session, Picture(takenLocal: taken.DateTime, offset: "+07:00")));

        Assert.False(uploaded.IsStale);
        Assert.InRange((taken - uploaded.TakenAt!.Value).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Same_file_is_rejected_the_second_time()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        var picture = Picture();

        (await UploadAsync(cashier, session, picture)).EnsureSuccessStatusCode();
        using var second = await UploadAsync(cashier, session, picture);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("уже загружали", (await ReadAsync<ErrorResponse>(second)).Message, StringComparison.Ordinal);
        Assert.Equal(1, await CountPhotosAsync());
        Assert.Single(StoredFiles());
    }

    [Theory]
    [InlineData("это не картинка, а просто текст")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"9\" height=\"9\"/></svg>")]
    [InlineData("push graphic-context\nviewbox 0 0 640 480\npop graphic-context")]
    public async Task File_that_is_not_a_photo_is_rejected(string content)
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);

        using var response = await UploadAsync(cashier, session, System.Text.Encoding.UTF8.GetBytes(content));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await CountPhotosAsync());
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task Broken_jpeg_is_rejected()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);

        // Начало настоящего JPEG, дальше мусор.
        var broken = Picture()[..40].Concat(Enumerable.Repeat((byte)0x55, 500)).ToArray();
        using var response = await UploadAsync(cashier, session, broken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task Empty_and_oversized_files_are_rejected()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);

        using var empty = await UploadAsync(cashier, session, []);
        using var huge = await UploadAsync(cashier, session, new byte[PhotoRules.MaxUploadBytes + 1]);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
    }

    [Fact]
    public async Task Unknown_link_is_rejected()
    {
        using var phone = app.CreateClient();

        using var status = await phone.GetAsync(Relative("/api/upload/no-such-token"), Ct);
        using var upload = await phone.PostAsync(Relative("/api/upload/no-such-token"), new ByteArrayContent(Picture()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.Contains("QR", (await ReadAsync<ErrorResponse>(upload)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_holds_at_most_ten_photos()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);

        for (var index = 0; index < PhotoRules.MaxPerSession; index++)
        {
            (await UploadAsync(cashier, session, Picture(width: 64, height: 64))).EnsureSuccessStatusCode();
        }

        using var extra = await UploadAsync(cashier, session, Picture(width: 64, height: 64));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, extra.StatusCode);
        Assert.Equal(PhotoRules.MaxPerSession, await CountPhotosAsync());
        Assert.Equal(PhotoRules.MaxPerSession, StoredFiles().Length);
    }

    [Fact]
    public async Task Photo_can_be_removed_from_the_phone()
    {
        using var cashier = app.CreateCashierClient();
        using var phone = app.CreateClient();
        var session = await CreateSessionAsync(cashier);
        var picture = Picture();
        var uploaded = await ReadAsync<PhotoResponse>(await UploadAsync(phone, session, picture));

        using var removed = await phone.DeleteAsync(Relative($"/api/upload/{Token(session)}/photos/{uploaded.Id}"), Ct);
        using var again = await phone.DeleteAsync(Relative($"/api/upload/{Token(session)}/photos/{uploaded.Id}"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(0, await CountPhotosAsync());
        Assert.Empty(StoredFiles());

        // Хеш удалённого снимка не мешает загрузить его снова.
        (await UploadAsync(phone, session, picture)).EnsureSuccessStatusCode();
    }

    // ---- Непривязанные фото ----

    [Fact]
    public async Task New_session_wipes_photos_that_were_never_sent()
    {
        using var cashier = app.CreateCashierClient();
        var first = await CreateSessionAsync(cashier);
        var picture = Picture();
        (await UploadAsync(cashier, first, picture)).EnsureSuccessStatusCode();
        (await UploadAsync(cashier, first, Picture())).EnsureSuccessStatusCode();

        var second = await CreateSessionAsync(cashier);

        Assert.Equal(0, await CountPhotosAsync());
        Assert.Empty(StoredFiles());
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync(Relative($"/api/photo-sessions/{first.Id}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(cashier, first, Picture())).StatusCode);

        // Тот же снимок можно загрузить заново — уже в новую сессию.
        (await UploadAsync(cashier, second, picture)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Cancel_removes_the_session_with_its_photos()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        (await UploadAsync(cashier, session, Picture())).EnsureSuccessStatusCode();

        using var cancelled = await cashier.DeleteAsync(Relative($"/api/photo-sessions/{session.Id}"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(0, await CountPhotosAsync());
        Assert.Empty(StoredFiles());
        Assert.Equal(0, await app.QueryDatabaseAsync(db => db.PhotoSessions.CountAsync(Ct)));
    }

    // ---- Смена ----

    [Fact]
    public async Task Shift_without_enough_photos_is_not_closed_and_keeps_the_session()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 2);

        using var withoutSession = await CloseShiftAsync(cashier, Shift(sessionId: null));
        using var rejected = await CloseShiftAsync(cashier, Shift(session.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutSession.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var problem = Assert.Single((await ReadAsync<ShiftRejectedResponse>(rejected)).Calculation.Problems);
        Assert.Equal(ShiftProblemCode.NotEnoughPhotos, problem.Code);
        Assert.Contains("загружено 2", problem.Message, StringComparison.Ordinal);
        Assert.Equal(0, await app.QueryDatabaseAsync(db => db.Shifts.CountAsync(Ct)));

        // Переснимать не нужно: догрузили третье фото и отправили снова.
        await UploadManyAsync(cashier, session, 1);
        using var closed = await CloseShiftAsync(cashier, Shift(session.Id));

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    [Fact]
    public async Task Preview_reports_missing_photos()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 2);

        var before = await PreviewAsync(cashier, Shift(session.Id));
        await UploadManyAsync(cashier, session, 1);
        var after = await PreviewAsync(cashier, Shift(session.Id));

        Assert.Equal([ShiftProblemCode.NotEnoughPhotos], before.Problems.Select(p => p.Code));
        Assert.True(after.CanClose);
    }

    [Fact]
    public async Task Closing_the_shift_attaches_photos_and_closes_the_session()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        var pictures = await UploadManyAsync(cashier, session, 4);

        using var closed = await CloseShiftAsync(cashier, Shift(session.Id));

        var shiftId = (await ReadAsync<ShiftClosedResponse>(closed)).ShiftId;
        var photos = await app.QueryDatabaseAsync(db => db.Photos.AsNoTracking().OrderBy(p => p.Id).ToListAsync(Ct));
        Assert.Equal(4, photos.Count);
        Assert.All(photos, photo => Assert.Equal(shiftId, photo.ShiftId));
        Assert.NotNull(await app.QueryDatabaseAsync(db => db.PhotoSessions.Select(s => s.ClosedAt).SingleAsync(Ct)));

        // Ссылка из QR больше не работает; касса тоже видит, что нужна новая сессия.
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(cashier, session, Picture())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync(Relative($"/api/upload/{Token(session)}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync(Relative($"/api/photo-sessions/{session.Id}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await cashier.DeleteAsync(Relative($"/api/upload/{Token(session)}/photos/{photos[0].Id}"), Ct)).StatusCode);

        // Новая сессия и «Отменить» привязанные фото не трогают.
        var next = await CreateSessionAsync(cashier);
        (await cashier.DeleteAsync(Relative($"/api/photo-sessions/{session.Id}"), Ct)).EnsureSuccessStatusCode();
        Assert.Equal(4, await CountPhotosAsync());
        Assert.Equal(4, StoredFiles().Length);

        // Снимок прошлой смены к следующей не приложить.
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(cashier, next, pictures[0])).StatusCode);
    }

    [Fact]
    public async Task Closed_session_cannot_feed_a_second_shift()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 3);
        (await CloseShiftAsync(cashier, Shift(session.Id))).EnsureSuccessStatusCode();

        // Сейф после первой смены вырос на 7000.
        using var second = await CloseShiftAsync(cashier, Shift(session.Id) with { FactSafe = OpeningSafe + 7000 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(1, await app.QueryDatabaseAsync(db => db.Shifts.CountAsync(Ct)));
    }

    [Fact]
    public async Task Repeated_request_does_not_need_the_photos_again()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 3);
        var key = Guid.CreateVersion7();

        using var first = await CloseShiftAsync(cashier, Shift(session.Id), key);
        using var replay = await CloseShiftAsync(cashier, Shift(session.Id), key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await ReadAsync<ShiftClosedResponse>(replay);
        Assert.True(replayed.Replayed);
        Assert.Equal((await ReadAsync<ShiftClosedResponse>(first)).ShiftId, replayed.ShiftId);
    }

    [Fact]
    public async Task Cancelled_shift_keeps_its_photos()
    {
        using var cashier = app.CreateCashierClient();
        using var root = await app.CreateRootClientAsync();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 3);
        (await CloseShiftAsync(cashier, Shift(session.Id))).EnsureSuccessStatusCode();
        var operationId = await app.QueryDatabaseAsync(db => db.Shifts.Select(s => s.OperationId).SingleAsync(Ct));

        using var cancelled = await root.PostAsJsonAsync($"/api/root/operations/{operationId}/cancel", new { reason = "ошибка ввода" }, Ct);

        cancelled.EnsureSuccessStatusCode();
        Assert.Equal(3, await CountPhotosAsync());
        Assert.Equal(3, StoredFiles().Length);
    }

    [Fact]
    public async Task Deleting_the_shift_by_hand_removes_photo_rows_but_not_files()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 3);
        (await CloseShiftAsync(cashier, Shift(session.Id))).EnsureSuccessStatusCode();

        await app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM operations WHERE id IN (SELECT operation_id FROM shifts)", Ct));

        Assert.Equal(0, await CountPhotosAsync());
        Assert.Equal(3, StoredFiles().Length);
    }

    // ---- Панель ----

    [Fact]
    public async Task Panel_shows_shift_photos_only_to_root()
    {
        using var cashier = app.CreateCashierClient();
        using var root = await app.CreateRootClientAsync();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 3);
        (await CloseShiftAsync(cashier, Shift(session.Id))).EnsureSuccessStatusCode();
        var operationId = await app.QueryDatabaseAsync(db => db.Shifts.Select(s => s.OperationId).SingleAsync(Ct));

        var detail = await GetAsync<OperationDetail>(root, $"/api/root/operations/{operationId}");

        Assert.Equal(3, detail.Shift!.Photos.Count);
        Assert.All(detail.Shift.Photos, photo => Assert.True(photo.IsStale)); // у тестовых снимков нет времени съёмки

        var photoId = detail.Shift.Photos[0].Id;
        using var picture = await root.GetAsync(Relative($"/api/root/photos/{photoId}"), Ct);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/jpeg", picture.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PhotoFormat.Jpeg, PhotoFormats.Detect(await picture.Content.ReadAsByteArrayAsync(Ct)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.GetAsync(Relative($"/api/root/photos/{photoId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await root.GetAsync(Relative($"/api/root/photos/{Guid.CreateVersion7()}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Panel_does_not_show_photos_that_are_not_attached_yet()
    {
        using var cashier = app.CreateCashierClient();
        using var root = await app.CreateRootClientAsync();
        var session = await CreateSessionAsync(cashier);
        var uploaded = await ReadAsync<PhotoResponse>(await UploadAsync(cashier, session, Picture()));

        using var picture = await root.GetAsync(Relative($"/api/root/photos/{uploaded.Id}"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, picture.StatusCode);
    }

    [Fact]
    public async Task Missing_file_is_404_not_a_server_error()
    {
        using var cashier = app.CreateCashierClient();
        using var root = await app.CreateRootClientAsync();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 3);
        (await CloseShiftAsync(cashier, Shift(session.Id))).EnsureSuccessStatusCode();
        var photo = await app.QueryDatabaseAsync(db => db.Photos.AsNoTracking().OrderBy(p => p.Id).FirstAsync(Ct));

        File.Delete(Path.Combine(app.PhotoDirectory, photo.Path));

        Assert.Equal(HttpStatusCode.NotFound, (await root.GetAsync(Relative($"/api/root/photos/{photo.Id}"), Ct)).StatusCode);
    }

    // ---- Telegram ----

    [Fact]
    public async Task Report_goes_as_the_caption_of_the_album()
    {
        await ConfigureTelegramAsync();
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        var fresh = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(-5), Moscow).DateTime;
        (await UploadAsync(cashier, session, Picture(takenLocal: fresh))).EnsureSuccessStatusCode();
        (await UploadAsync(cashier, session, Picture(takenLocal: fresh))).EnsureSuccessStatusCode();
        (await UploadAsync(cashier, session, Picture())).EnsureSuccessStatusCode(); // без времени съёмки

        (await CloseShiftAsync(cashier, Shift(session.Id))).EnsureSuccessStatusCode();
        var sent = await DispatchAsync();

        // Отчёт — подпись к альбому; отдельного текста в тему отчётов нет.
        Assert.Equal(1, sent);
        Assert.Empty(app.Telegram.Sent);

        var album = Assert.Single(app.Telegram.SentPhotos);
        Assert.Equal((AdminChat, ReportsThread), (album.ChatId, album.ThreadId));
        Assert.Contains("Выручка: ", album.Caption, StringComparison.Ordinal);
        Assert.EndsWith("Фото: 3", album.Caption, StringComparison.Ordinal);    // давность снимков в отчёт не пишется
        Assert.Equal(3, album.FileSizes.Length);
        Assert.All(album.FileSizes, size => Assert.True(size > 0));
    }

    [Fact]
    public async Task Report_too_long_for_a_caption_follows_the_album()
    {
        await ConfigureTelegramAsync();
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 3);

        // Три сотрудника с именами по 100 символов и причина на 500 символов
        // раздувают отчёт сверх 1024 символов подписи.
        var employees = await app.QueryDatabaseAsync(async db =>
        {
            var created = Enumerable.Range(0, 3)
                .Select(index => Employee.Create(2001 + index, new string((char)('а' + index), Employee.NameLength), 200))
                .ToList();
            db.Employees.AddRange(created);
            await db.SaveChangesAsync(Ct);
            return created.ConvertAll(static employee => employee.Id);
        });
        var reason = new string('я', ShiftCalculator.MaxReasonLength);
        var input = Shift(session.Id) with
        {
            ShortageReason = reason,
            Employees = [.. employees.Select(static id => new ShiftEmployeeInput(id, 4, 0))],
        };
        (await CloseShiftAsync(cashier, input)).EnsureSuccessStatusCode();
        await DispatchAsync();

        Assert.Equal(string.Empty, Assert.Single(app.Telegram.SentPhotos).Caption);
        Assert.Contains(reason, Assert.Single(app.Telegram.Sent).Text, StringComparison.Ordinal);

        // Альбом стоит в очереди перед текстом.
        var queue = await app.QueryDatabaseAsync(db => db.OutboxMessages.AsNoTracking().OrderBy(m => m.Sequence).ToListAsync(Ct));
        Assert.Equal([true, false], queue.Select(m => m.PhotosShiftId is not null));
    }

    [Fact]
    public async Task Album_with_a_lost_file_is_a_permanent_failure()
    {
        await ConfigureTelegramAsync();
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        await UploadManyAsync(cashier, session, 3);
        (await CloseShiftAsync(cashier, Shift(session.Id))).EnsureSuccessStatusCode();

        // Настоящий клиент Telegram: до сети дело не доходит — файл не открывается раньше.
        foreach (var file in StoredFiles())
        {
            File.Delete(file);
        }

        using var telegram = new TelegramClient(Microsoft.Extensions.Options.Options.Create(new TelegramOptions { BotToken = "0:test" }));
        var paths = await app.QueryDatabaseAsync(db => db.Photos.Select(p => p.Path).ToListAsync(Ct));

        var result = await telegram.SendPhotosAsync(
            AdminChat, ReportsThread, [.. paths.Select(path => Path.Combine(app.PhotoDirectory, path))], "подпись", Ct);

        Assert.Equal(TelegramOutcome.Permanent, result.Outcome);
    }

    [Fact]
    public async Task Shift_without_photos_sends_no_album()
    {
        await ConfigureTelegramAsync();
        await app.QueryDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE club_settings SET min_shift_photos = 0", Ct));
        using var cashier = app.CreateCashierClient();

        (await CloseShiftAsync(cashier, Shift(sessionId: null))).EnsureSuccessStatusCode();
        await DispatchAsync();

        Assert.DoesNotContain("Фото", Assert.Single(app.Telegram.Sent).Text, StringComparison.Ordinal);
        Assert.Empty(app.Telegram.SentPhotos);
    }

    // ---- Вставка с кассы ----

    [Fact]
    public async Task Screenshot_pasted_at_the_cashier_is_accepted_without_a_time_check()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);

        using var response = await PasteAsync(cashier, session, Picture(format: MagickFormat.Png));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pasted = await ReadAsync<PhotoResponse>(response);
        Assert.True(pasted.PastedAtCashier);
        Assert.False(pasted.IsStale);     // времени съёмки у снимка экрана нет — и это не повод для отметки
        Assert.Null(pasted.TakenAt);

        // Телефон видит и вставленные снимки; снятые им самим отмечаются по-прежнему.
        var fromPhone = await ReadAsync<PhotoResponse>(await UploadAsync(cashier, session, Picture()));
        Assert.False(fromPhone.PastedAtCashier);
        Assert.True(fromPhone.IsStale);

        var status = await GetAsync<UploadStatusResponse>(cashier, $"/api/upload/{Token(session)}");
        Assert.Equal([true, false], status.Photos.Select(p => p.PastedAtCashier));
    }

    [Fact]
    public async Task Pasting_requires_the_cashier_token()
    {
        using var cashier = app.CreateCashierClient();
        using var anonymous = app.CreateClient();
        using var root = await app.CreateRootClientAsync();
        var session = await CreateSessionAsync(cashier);

        // Секрет из QR даёт право только на загрузку «как с телефона».
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasteAsync(anonymous, session, Picture())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasteAsync(root, session, Picture())).StatusCode);
        Assert.Equal(0, await CountPhotosAsync());
    }

    [Fact]
    public async Task Pasted_screenshots_follow_the_same_rules_as_phone_photos()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        var picture = Picture();

        (await PasteAsync(cashier, session, picture)).EnsureSuccessStatusCode();

        // Повтор — и вставкой, и с телефона; не картинка; несуществующая сессия.
        Assert.Equal(HttpStatusCode.Conflict, (await PasteAsync(cashier, session, picture)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(cashier, session, picture)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PasteAsync(cashier, session, "текст"u8.ToArray())).StatusCode);
        using var gone = await cashier.PostAsync(
            Relative($"/api/photo-sessions/{Guid.CreateVersion7()}/photos"), new ByteArrayContent(Picture()), Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("Вставить фото", (await ReadAsync<ErrorResponse>(gone)).Message, StringComparison.Ordinal);

        Assert.Equal(1, await CountPhotosAsync());
        Assert.Single(StoredFiles());
    }

    [Fact]
    public async Task Pasted_screenshot_can_be_removed_at_the_cashier()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        var pasted = await ReadAsync<PhotoResponse>(await PasteAsync(cashier, session, Picture()));

        using var removed = await cashier.DeleteAsync(Relative($"/api/photo-sessions/{session.Id}/photos/{pasted.Id}"), Ct);
        using var again = await cashier.DeleteAsync(Relative($"/api/photo-sessions/{session.Id}/photos/{pasted.Id}"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(0, await CountPhotosAsync());
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task Shift_can_be_closed_with_pasted_screenshots_and_the_report_counts_them_separately()
    {
        await ConfigureTelegramAsync();
        using var cashier = app.CreateCashierClient();
        using var root = await app.CreateRootClientAsync();
        var session = await CreateSessionAsync(cashier);
        (await PasteAsync(cashier, session, Picture())).EnsureSuccessStatusCode();
        (await PasteAsync(cashier, session, Picture())).EnsureSuccessStatusCode();
        var fresh = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(-5), Moscow).DateTime;
        (await UploadAsync(cashier, session, Picture(takenLocal: fresh))).EnsureSuccessStatusCode();

        (await CloseShiftAsync(cashier, Shift(session.Id))).EnsureSuccessStatusCode();
        await DispatchAsync();

        var album = Assert.Single(app.Telegram.SentPhotos);
        Assert.EndsWith("Фото: 3 (с кассы: 2)", album.Caption, StringComparison.Ordinal);
        Assert.Equal(3, album.FileSizes.Length);

        // В панели вставленные снимки отличимы от снятых телефоном.
        var operationId = await app.QueryDatabaseAsync(db => db.Shifts.Select(s => s.OperationId).SingleAsync(Ct));
        var detail = await GetAsync<OperationDetail>(root, $"/api/root/operations/{operationId}");
        Assert.Equal([true, true, false], detail.Shift!.Photos.Select(p => p.PastedAtCashier));

        // После закрытия смены вставить в ту же сессию уже нельзя.
        Assert.Equal(HttpStatusCode.NotFound, (await PasteAsync(cashier, session, Picture())).StatusCode);
    }

    [Fact]
    public async Task Database_rejects_a_pasted_photo_marked_stale()
    {
        using var cashier = app.CreateCashierClient();
        var session = await CreateSessionAsync(cashier);
        (await PasteAsync(cashier, session, Picture())).EnsureSuccessStatusCode();

        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.QueryDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE photos SET is_stale = true", Ct)));

        Assert.Equal("ck_photos_pasted_not_stale", error.ConstraintName);
    }

    // ---- Вспомогательное ----

    private static async Task<HttpResponseMessage> PasteAsync(HttpClient client, PhotoSessionResponse session, byte[] file)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/photo-sessions/{session.Id}/photos") { Content = new ByteArrayContent(file) };
        return await client.SendAsync(request, Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static string Token(PhotoSessionResponse session) => session.UploadUrl[(session.UploadUrl.LastIndexOf('/') + 1)..];

    /// <summary>Смена без расхождений; в сейф уходит 7000.</summary>
    private ShiftInput Shift(Guid? sessionId) => new()
    {
        FactCash = 8000,
        FactNonCash = 12000,
        SkladCash = 2000,
        SkladNonCash = 3000,
        LangameCash = 6000,
        LangameNonCash = 9000,
        FactSafe = OpeningSafe,
        Employees = [new(_ivan, 8, 0)],
        PhotoSessionId = sessionId,
    };

    /// <summary>
    /// Картинка, не похожая на предыдущие: у каждой свой цвет, поэтому свой хеш.
    /// Время съёмки записывается в EXIF так же, как это делает камера телефона.
    /// </summary>
    private byte[] Picture(
        uint width = 640,
        uint height = 480,
        DateTime? takenLocal = null,
        string? offset = null,
        MagickFormat format = MagickFormat.Jpeg)
    {
        var seed = ++_seed;
        using var image = new MagickImage(new MagickColor((byte)(seed * 37), (byte)(seed * 91), (byte)(255 - (seed * 13))), width, height);

        if (takenLocal is { } taken)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.DateTimeOriginal, taken.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
            if (offset is not null)
            {
                exif.SetValue(ExifTag.OffsetTimeOriginal, offset);
            }

            image.SetProfile(exif);
        }

        return image.ToByteArray(format);
    }

    private async Task<List<byte[]>> UploadManyAsync(HttpClient client, PhotoSessionResponse session, int count)
    {
        var pictures = new List<byte[]>(count);
        for (var index = 0; index < count; index++)
        {
            var picture = Picture();
            (await UploadAsync(client, session, picture)).EnsureSuccessStatusCode();
            pictures.Add(picture);
        }

        return pictures;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, PhotoSessionResponse session, byte[] file)
    {
        // Загрузке токен кассы не нужен — только секрет из ссылки.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/upload/{Token(session)}") { Content = new ByteArrayContent(file) };
        return await client.SendAsync(request, Ct);
    }

    private static async Task<PhotoSessionResponse> CreateSessionAsync(HttpClient cashier)
    {
        using var response = await cashier.PostAsync(Relative("/api/photo-sessions"), null, Ct);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<PhotoSessionResponse>(response);
    }

    private static async Task<HttpResponseMessage> CloseShiftAsync(HttpClient client, ShiftInput input, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shifts") { Content = JsonContent.Create(input, options: Json) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.CreateVersion7()).ToString());
        return await client.SendAsync(request, Ct);
    }

    private static async Task<ShiftCalculationResponse> PreviewAsync(HttpClient client, ShiftInput input)
    {
        using var response = await client.PostAsJsonAsync("/api/shifts/calc", input, Json, Ct);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<ShiftCalculationResponse>(response);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path) =>
        await client.GetFromJsonAsync<T>(Relative(path), Json, Ct)
        ?? throw new InvalidOperationException($"Пустой ответ от {path}.");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(Json, Ct)
        ?? throw new InvalidOperationException("Пустое тело ответа.");

    private Task<int> CountPhotosAsync() => app.QueryDatabaseAsync(db => db.Photos.CountAsync(Ct));

    private string[] StoredFiles() =>
        Directory.Exists(app.PhotoDirectory) ? Directory.GetFiles(app.PhotoDirectory, "*", SearchOption.AllDirectories) : [];

    private Task<int> ConfigureTelegramAsync() => app.QueryDatabaseAsync(db => db.Database.ExecuteSqlAsync(
        $"UPDATE club_settings SET telegram_chat_id = {AdminChat}, telegram_reports_thread_id = {ReportsThread}", Ct));

    private Task<int> DispatchAsync() => app.InScopeAsync(services =>
        services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync(Ct));
}
