using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace VideoFactory.Bot.Configuration;

/// <summary>
/// Загружает локальный файл <c>.env</c> как источник конфигурации с приоритетом ниже
/// переменных окружения и аргументов командной строки.
/// </summary>
public static class DotEnvLoader
{
    /// <summary>
    /// Добавляет существующий файл <c>.env</c> в конфигурацию.
    /// </summary>
    /// <param name="configuration">Строитель конфигурации.</param>
    /// <param name="path">Путь к локальному файлу.</param>
    /// <returns><see langword="true"/>, если файл был прочитан; иначе <see langword="false"/>.</returns>
    public static bool Add(IConfigurationBuilder configuration, string path)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return false;
        }

        Dictionary<string, string?> values = Parse(File.ReadAllLines(path));
        MemoryConfigurationSource source = new() { InitialData = values };
        int? environmentIndex = configuration.Sources
            .Select((item, index) => (item, index))
            .Where(pair => pair.item is EnvironmentVariablesConfigurationSource)
            .Select(pair => (int?)pair.index)
            .LastOrDefault();

        if (environmentIndex is int index)
        {
            configuration.Sources.Insert(index, source);
        }
        else
        {
            configuration.Add(source);
        }

        if (configuration is IConfigurationRoot root)
        {
            root.Reload();
        }

        return true;
    }

    /// <summary>
    /// Ищет <c>.env</c> в текущем каталоге и его родительских каталогах.
    /// </summary>
    /// <param name="startDirectory">Каталог, с которого начинается поиск.</param>
    /// <returns>Путь к найденному файлу или <see langword="null"/>.</returns>
    public static string? Find(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        DirectoryInfo? directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static Dictionary<string, string?> Parse(IEnumerable<string> lines)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string originalLine in lines)
        {
            string line = originalLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim().Replace("__", ":", StringComparison.Ordinal);
            if (key.Length == 0)
            {
                continue;
            }

            string value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }
}
