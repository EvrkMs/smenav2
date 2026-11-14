using System.Text.Json;

namespace Smena.Client;

/// <summary>
/// Положение и размер окна между запусками: администратор один раз расставил
/// окно — оно так и открывается. Хранится рядом с настройками подключения.
/// </summary>
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    private static readonly string FilePath = Path.Combine(ConnectionSettings.DataDirectory, "window.json");

    public Rectangle Bounds => new(X, Y, Width, Height);

    public static WindowPlacement? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConnectionSettings.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не сохранилось — в следующий раз окно откроется в размере по умолчанию.
        }
    }
}
