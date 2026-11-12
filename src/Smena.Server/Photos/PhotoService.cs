using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Smena.Core.Photos;
using Smena.Server.Database;

namespace Smena.Server.Photos;

public enum PhotoUploadStatus
{
    Saved,

    /// <summary>Сессии с таким токеном нет или она уже закрыта.</summary>
    SessionGone,

    /// <summary>Файл — не изображение, которое сервер умеет читать.</summary>
    NotAnImage,

    /// <summary>Этот же файл уже загружен — в эту сессию или к одной из прошлых смен.</summary>
    Duplicate,

    TooMany,
}

public readonly record struct PhotoUploadResult(PhotoUploadStatus Status, Photo? Photo = null);

/// <summary>
/// Сессии загрузки и сами фото.
/// </summary>
/// <remarks>
/// Правило, на котором всё держится: фото открытой сессии ни к чему не привязаны,
/// фото закрытой — привязаны к смене. Поэтому «стереть непривязанное» значит
/// «удалить открытые сессии», а загрузка в закрытую сессию невозможна.
/// Каждое действие, меняющее сессию, сначала блокирует её строку: загрузка,
/// закрытие смены и отмена не могут пройти одновременно и оставить фото «между».
/// </remarks>
public sealed class PhotoService(SmenaDbContext db, PhotoStorage storage, PhotoProcessor processor, TimeProvider time)
{
    /// <summary>
    /// Создаёт сессию для новой смены. Касса одна, поэтому открытая сессия всегда
    /// одна: прежние открытые сессии удаляются вместе с фото, которые так и не
    /// были отправлены с отчётом.
    /// </summary>
    public async Task<PhotoSession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var orphans = await DeleteOpenSessionsAsync(only: null, cancellationToken);

        var session = PhotoSession.Create(time.GetUtcNow());
        db.PhotoSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Файлы — после фиксации: при откате строки останутся, и файлы должны остаться с ними.
        storage.Delete(orphans);
        return session;
    }

    /// <summary>«Отменить» на кассе: сессия и её фото удаляются. Закрытую сессию не трогает.</summary>
    public async Task CancelSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var orphans = await DeleteOpenSessionsAsync(sessionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        storage.Delete(orphans);
    }

    public Task<PhotoSession?> FindOpenAsync(Guid sessionId, CancellationToken cancellationToken) =>
        db.PhotoSessions.AsNoTracking().FirstOrDefaultAsync(session => session.Id == sessionId && session.ClosedAt == null, cancellationToken);

    public Task<PhotoSession?> FindOpenAsync(string token, CancellationToken cancellationToken) =>
        db.PhotoSessions.AsNoTracking().FirstOrDefaultAsync(session => session.Token == token && session.ClosedAt == null, cancellationToken);

    /// <summary>Фото сессии в порядке загрузки (идентификаторы — UUID v7, они растут со временем).</summary>
    public Task<List<Photo>> ListAsync(Guid sessionId, CancellationToken cancellationToken) =>
        db.Photos.AsNoTracking().Where(photo => photo.SessionId == sessionId).OrderBy(static photo => photo.Id).ToListAsync(cancellationToken);

    /// <summary>
    /// Сколько фото ждёт отправки в сессии. У закрытой или несуществующей сессии — ноль:
    /// её фото уже привязаны к другой смене.
    /// </summary>
    public Task<int> CountPendingAsync(Guid? sessionId, CancellationToken cancellationToken) =>
        sessionId is { } id
            ? db.Photos.CountAsync(photo => photo.SessionId == id && photo.ShiftId == null, cancellationToken)
            : Task.FromResult(0);

    /// <summary>Снимок с телефона: сессия определяется секретом из ссылки в QR.</summary>
    public async Task<PhotoUploadResult> UploadAsync(string token, MemoryStream original, CancellationToken cancellationToken) =>
        await UploadAsync(await FindOpenAsync(token, cancellationToken), original, pastedAtCashier: false, cancellationToken);

    /// <summary>Снимок экрана, вставленный на кассе из буфера обмена.</summary>
    public async Task<PhotoUploadResult> PasteAsync(Guid sessionId, MemoryStream original, CancellationToken cancellationToken) =>
        await UploadAsync(await FindOpenAsync(sessionId, cancellationToken), original, pastedAtCashier: true, cancellationToken);

    private async Task<PhotoUploadResult> UploadAsync(
        PhotoSession? session,
        MemoryStream original,
        bool pastedAtCashier,
        CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return new PhotoUploadResult(PhotoUploadStatus.SessionGone);
        }

        // Хеш исходного файла, до пережатия: цель — отсечь повторную отправку того же
        // файла. Похожие кадры (два снимка одного экрана) повтором не считаются.
        var file = original.GetBuffer().AsMemory(0, (int)original.Length);
        var hash = SHA256.HashData(file.Span);
        if (await db.Photos.AnyAsync(photo => photo.Sha256 == hash, cancellationToken))
        {
            return new PhotoUploadResult(PhotoUploadStatus.Duplicate);
        }

        var settings = await db.ClubSettings.AsNoTracking().SingleAsync(cancellationToken);

        // Тяжёлая часть — вне транзакции: блокировка сессии держится только на время записи.
        if (await processor.ProcessAsync(file, settings.GetTimeZone(), cancellationToken) is not { } processed)
        {
            return new PhotoUploadResult(PhotoUploadStatus.NotAnImage);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockOpenSessionAsync(session.Id, cancellationToken))
        {
            // Пока снимок обрабатывался, смену закрыли или сессию отменили.
            return new PhotoUploadResult(PhotoUploadStatus.SessionGone);
        }

        if (await db.Photos.CountAsync(photo => photo.SessionId == session.Id, cancellationToken) >= PhotoRules.MaxPerSession)
        {
            return new PhotoUploadResult(PhotoUploadStatus.TooMany);
        }

        var now = time.GetUtcNow();
        var photoId = Guid.CreateVersion7();
        var path = await storage.SaveAsync(photoId, now, processed.Jpeg, cancellationToken);

        try
        {
            var photo = Photo.Create(
                photoId,
                session.Id,
                hash,
                path,
                processed.Jpeg.Length,
                processed.Width,
                processed.Height,
                processed.TakenAt,
                // У снимка экрана времени съёмки нет по природе — это не признак подлога.
                isStale: !pastedAtCashier && PhotoRules.IsStale(processed.TakenAt, now, settings.PhotoMaxAgeHours),
                now,
                pastedAtCashier);

            db.Photos.Add(photo);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new PhotoUploadResult(PhotoUploadStatus.Saved, photo);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Тот же файл успели загрузить параллельно (двойное нажатие): уникальность хеша держит база.
            storage.Delete([path]);
            return new PhotoUploadResult(PhotoUploadStatus.Duplicate);
        }
        catch
        {
            storage.Delete([path]);
            throw;
        }
    }

    /// <summary>Убирает один снимок из открытой сессии: администратор снял не то.</summary>
    public async Task<bool> RemoveAsync(string token, Guid photoId, CancellationToken cancellationToken) =>
        await RemoveAsync(await FindOpenAsync(token, cancellationToken), photoId, cancellationToken);

    /// <summary>То же с кассы: сессия известна по идентификатору.</summary>
    public async Task<bool> RemoveAsync(Guid sessionId, Guid photoId, CancellationToken cancellationToken) =>
        await RemoveAsync(await FindOpenAsync(sessionId, cancellationToken), photoId, cancellationToken);

    private async Task<bool> RemoveAsync(PhotoSession? session, Guid photoId, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockOpenSessionAsync(session.Id, cancellationToken))
        {
            return false;
        }

        var path = await db.Photos
            .Where(photo => photo.Id == photoId && photo.SessionId == session.Id)
            .Select(static photo => photo.Path)
            .FirstOrDefaultAsync(cancellationToken);

        if (path is null)
        {
            return false;
        }

        await db.Photos.Where(photo => photo.Id == photoId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        storage.Delete([path]);
        return true;
    }

    /// <summary>
    /// Блокирует сессию до конца текущей транзакции: с этого момента в неё нельзя
    /// ни загрузить, ни удалить фото. Вызывается при закрытии смены перед подсчётом фото.
    /// </summary>
    /// <returns>false, если открытой сессии с таким идентификатором нет.</returns>
    public async Task<bool> LockOpenSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        RequireTransaction();

        var locked = await db.PhotoSessions
            .FromSql($"SELECT * FROM photo_sessions WHERE id = {sessionId} AND closed_at IS NULL FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return locked.Count > 0;
    }

    /// <summary>
    /// Привязывает фото сессии к смене и закрывает сессию. Изменения только кладутся
    /// в контекст: в базу они попадают при SaveChanges вызывающего, вместе со сменой.
    /// </summary>
    /// <remarks>Сессия должна быть заблокирована через <see cref="LockOpenSessionAsync"/>.</remarks>
    public async Task<IReadOnlyList<Photo>> AttachToShiftAsync(Guid sessionId, Guid shiftId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireTransaction();

        var session = await db.PhotoSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.ClosedAt == null, cancellationToken);
        if (session is null)
        {
            return [];
        }

        var photos = await db.Photos
            .Where(photo => photo.SessionId == sessionId)
            .OrderBy(static photo => photo.Id)
            .ToListAsync(cancellationToken);

        foreach (var photo in photos)
        {
            photo.AttachTo(shiftId);
        }

        session.Close(now);
        return photos;
    }

    /// <summary>Удаляет открытые сессии (все или одну) и возвращает пути файлов их фото.</summary>
    private async Task<List<string>> DeleteOpenSessionsAsync(Guid? only, CancellationToken cancellationToken)
    {
        // Сначала блокировка, потом чтение путей: иначе загрузка, идущая в эту же
        // секунду, добавила бы фото после чтения, и его файл остался бы на диске.
        var sessions = only is { } id
            ? db.PhotoSessions.FromSql($"SELECT * FROM photo_sessions WHERE id = {id} AND closed_at IS NULL FOR UPDATE")
            : db.PhotoSessions.FromSql($"SELECT * FROM photo_sessions WHERE closed_at IS NULL FOR UPDATE");

        var locked = await sessions.AsNoTracking().ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            return [];
        }

        var ids = locked.ConvertAll(static session => session.Id);

        var paths = await db.Photos
            .Where(photo => ids.Contains(photo.SessionId))
            .Select(static photo => photo.Path)
            .ToListAsync(cancellationToken);

        // Строки фото удаляет каскад.
        await db.PhotoSessions.Where(session => ids.Contains(session.Id)).ExecuteDeleteAsync(cancellationToken);
        return paths;
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Сессию фото меняют только внутри транзакции базы.");
        }
    }
}
