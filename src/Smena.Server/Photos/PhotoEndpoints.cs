using System.Buffers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Smena.Core.Photos;
using Smena.Server.Cashier;
using Smena.Server.Database;

namespace Smena.Server.Photos;

public sealed record PhotoResponse(Guid Id, DateTimeOffset? TakenAt, bool IsStale, DateTimeOffset UploadedAt)
{
    public static PhotoResponse From(Photo photo) => new(photo.Id, photo.TakenAt, photo.IsStale, photo.UploadedAt);
}

/// <param name="UploadUrl">Ссылка для QR: страница загрузки, открывается с телефона.</param>
public sealed record PhotoSessionResponse(Guid Id, string UploadUrl, int MinPhotos, int MaxPhotos, IReadOnlyList<PhotoResponse> Photos);

/// <summary>То, что видит страница загрузки на телефоне.</summary>
public sealed record UploadStatusResponse(int MinPhotos, int MaxPhotos, IReadOnlyList<PhotoResponse> Photos);

/// <summary>
/// Фото к смене. Две стороны: касса (токен устройства) создаёт сессию и следит за
/// числом фото; телефон (секрет сессии в ссылке из QR) загружает снимки.
/// </summary>
internal static class PhotoEndpoints
{
    private const string SessionGoneMessage =
        "Ссылка больше не действует: смена уже закрыта или QR показан заново. Отсканируйте QR на кассе ещё раз.";

    public static IServiceCollection AddPhotos(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<PhotoOptions>, PhotoOptionsValidator>();
        services.AddOptions<PhotoOptions>().BindConfiguration(PhotoOptions.SectionName).ValidateOnStart();
        services.AddSingleton<PhotoStorage>();
        services.AddSingleton<PhotoProcessor>();
        services.AddScoped<PhotoService>();
        return services;
    }

    public static IEndpointRouteBuilder MapPhotoApi(this IEndpointRouteBuilder routes)
    {
        var cashier = routes.MapGroup("/api/photo-sessions").RequireAuthorization(DeviceAuthenticationHandler.CashierPolicy);
        cashier.MapPost("/", CreateSessionAsync);
        cashier.MapGet("/{id:guid}", GetSessionAsync);
        cashier.MapDelete("/{id:guid}", CancelSessionAsync);

        // Без входа: право на эти адреса даёт секрет сессии в самом адресе.
        var upload = routes.MapGroup("/api/upload/{token}");
        upload.MapGet("/", GetStatusAsync);
        upload.MapPost("/", UploadAsync);
        upload.MapGet("/photos/{id:guid}", GetUploadedPhotoAsync);
        upload.MapDelete("/photos/{id:guid}", RemovePhotoAsync);

        // Страница одна на все сессии; секрет она берёт из своего адреса.
        routes.MapGet("/u/{token}", static (HttpContext context, IWebHostEnvironment environment) =>
        {
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            return TypedResults.PhysicalFile(
                Path.Combine(environment.WebRootPath, "upload", "index.html"),
                "text/html; charset=utf-8");
        });

        return routes;
    }

    /// <summary>Отдаёт сохранённый снимок. Файла нет на диске (удалён руками) — 404, а не ошибка сервера.</summary>
    public static IResult PhotoFile(PhotoStorage storage, string relativePath, HttpContext context)
    {
        var path = storage.FullPath(relativePath);
        if (!File.Exists(path))
        {
            return TypedResults.NotFound();
        }

        // Снимок после сохранения не меняется, а адрес содержит его идентификатор.
        context.Response.Headers[HeaderNames.CacheControl] = "private, max-age=86400, immutable";
        return TypedResults.PhysicalFile(path, "image/jpeg");
    }

    // ---- Касса ----

    private static async Task<Ok<PhotoSessionResponse>> CreateSessionAsync(
        HttpContext context,
        PhotoService photos,
        SmenaDbContext db,
        IOptions<PhotoOptions> options,
        CancellationToken cancellationToken)
    {
        var session = await photos.CreateSessionAsync(cancellationToken);
        return TypedResults.Ok(await ToResponseAsync(session, [], context, db, options.Value, cancellationToken));
    }

    private static async Task<Results<Ok<PhotoSessionResponse>, NotFound>> GetSessionAsync(
        Guid id,
        HttpContext context,
        PhotoService photos,
        SmenaDbContext db,
        IOptions<PhotoOptions> options,
        CancellationToken cancellationToken)
    {
        // Закрытая сессия для кассы — то же, что отсутствующая: нужна новая.
        if (await photos.FindOpenAsync(id, cancellationToken) is not { } session)
        {
            return TypedResults.NotFound();
        }

        var list = await photos.ListAsync(session.Id, cancellationToken);
        return TypedResults.Ok(await ToResponseAsync(session, list, context, db, options.Value, cancellationToken));
    }

    private static async Task<NoContent> CancelSessionAsync(Guid id, PhotoService photos, CancellationToken cancellationToken)
    {
        await photos.CancelSessionAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<PhotoSessionResponse> ToResponseAsync(
        PhotoSession session,
        List<Photo> photos,
        HttpContext context,
        SmenaDbContext db,
        PhotoOptions options,
        CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? $"{context.Request.Scheme}://{context.Request.Host}"
            : options.PublicBaseUrl.TrimEnd('/');

        return new PhotoSessionResponse(
            session.Id,
            $"{baseUrl}/u/{session.Token}",
            await MinPhotosAsync(db, cancellationToken),
            PhotoRules.MaxPerSession,
            photos.ConvertAll(PhotoResponse.From));
    }

    // ---- Телефон ----

    private static async Task<Results<Ok<UploadStatusResponse>, NotFound<ErrorResponse>>> GetStatusAsync(
        string token,
        PhotoService photos,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        if (await photos.FindOpenAsync(token, cancellationToken) is not { } session)
        {
            return TypedResults.NotFound(new ErrorResponse(SessionGoneMessage));
        }

        var list = await photos.ListAsync(session.Id, cancellationToken);
        return TypedResults.Ok(new UploadStatusResponse(
            await MinPhotosAsync(db, cancellationToken),
            PhotoRules.MaxPerSession,
            list.ConvertAll(PhotoResponse.From)));
    }

    /// <summary>Тело запроса — сам файл, без multipart: страница шлёт снимки по одному.</summary>
    private static async Task<IResult> UploadAsync(
        string token,
        HttpRequest request,
        PhotoService photos,
        CancellationToken cancellationToken)
    {
        using var original = await ReadBodyAsync(request, PhotoRules.MaxUploadBytes, cancellationToken);
        if (original is null)
        {
            return TypedResults.Json(
                new ErrorResponse($"Файл больше {PhotoRules.MaxUploadBytes / (1024 * 1024)} МБ."),
                ApiJsonContext.Default.ErrorResponse,
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (original.Length == 0)
        {
            return TypedResults.BadRequest(new ErrorResponse("Файл пустой."));
        }

        var result = await photos.UploadAsync(token, original, cancellationToken);

        return result.Status switch
        {
            PhotoUploadStatus.Saved => TypedResults.Ok(PhotoResponse.From(result.Photo!)),
            PhotoUploadStatus.SessionGone => TypedResults.NotFound(new ErrorResponse(SessionGoneMessage)),
            PhotoUploadStatus.Duplicate => TypedResults.Conflict(new ErrorResponse(
                "Это фото уже загружали раньше. Сделайте новый снимок.")),
            PhotoUploadStatus.TooMany => TypedResults.UnprocessableEntity(new ErrorResponse(
                $"К смене можно приложить не больше {PhotoRules.MaxPerSession} фото.")),
            _ => TypedResults.UnprocessableEntity(new ErrorResponse(
                "Файл не похож на фото. Подойдёт обычный снимок камеры (JPEG) или PNG.")),
        };
    }

    private static async Task<IResult> GetUploadedPhotoAsync(
        string token,
        Guid id,
        HttpContext context,
        PhotoService photos,
        PhotoStorage storage,
        SmenaDbContext db,
        CancellationToken cancellationToken)
    {
        if (await photos.FindOpenAsync(token, cancellationToken) is not { } session)
        {
            return TypedResults.NotFound();
        }

        var path = await db.Photos
            .Where(photo => photo.Id == id && photo.SessionId == session.Id)
            .Select(static photo => photo.Path)
            .FirstOrDefaultAsync(cancellationToken);

        return path is null ? TypedResults.NotFound() : PhotoFile(storage, path, context);
    }

    private static async Task<Results<NoContent, NotFound>> RemovePhotoAsync(
        string token,
        Guid id,
        PhotoService photos,
        CancellationToken cancellationToken) =>
        await photos.RemoveAsync(token, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static Task<int> MinPhotosAsync(SmenaDbContext db, CancellationToken cancellationToken) =>
        db.ClubSettings.AsNoTracking().Select(static settings => settings.MinShiftPhotos).SingleAsync(cancellationToken);

    /// <returns>null, если тело больше <paramref name="maxBytes"/>.</returns>
    private static async Task<MemoryStream?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        var result = new MemoryStream(capacity: (int)(request.ContentLength ?? 0));
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                // Заголовку длины верить нельзя: его может не быть или он может врать.
                if (result.Length + read > maxBytes)
                {
                    await result.DisposeAsync();
                    return null;
                }

                result.Write(chunk, 0, read);
            }
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return result;
    }
}
