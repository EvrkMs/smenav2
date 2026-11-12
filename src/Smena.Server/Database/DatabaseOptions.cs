using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Smena.Server.Database;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Применять недостающие миграции при старте сервера.</summary>
    public bool MigrateOnStartup { get; set; } = true;
}

/// <summary>
/// Проверка настроек, сгенерированная на этапе компиляции (без рефлексии).
/// Вместе с ValidateOnStart сервер не запустится с пустой строкой подключения,
/// а не упадёт на первом запросе к базе.
/// </summary>
[OptionsValidator]
internal sealed partial class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>;
