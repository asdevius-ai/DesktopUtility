using System.Globalization;
using System.Windows.Markup;

namespace DesktopUtility.Host.Core;

/// <summary>Tiny UI localization: Ukrainian when Windows UI language is Ukrainian, English otherwise.</summary>
public static class Loc
{
    private static readonly bool Ukrainian =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("uk", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, (string En, string Uk)> Strings = new()
    {
        ["status.starting"] = ("Starting…", "Запуск…"),
        ["status.ready"] = ("Ready to pair", "Готово до підключення"),
        ["status.failed"] = ("Could not start", "Не вдалося запустити"),
        ["status.streaming"] = ("Streaming", "Трансляція активна"),

        ["pair.title"] = ("Pair your iPad", "Підключіть iPad"),
        ["pair.pin"] = ("PIN CODE", "PIN-КОД"),
        ["pair.step1"] = ("Scan the QR code with the iPad camera", "Скануйте QR-код камерою iPad"),
        ["pair.step2"] = ("Open the link, then Share → Add to Home Screen", "Відкрийте посилання, потім «Поділитись» → «На початковий екран»"),
        ["pair.new"] = ("New code", "Новий код"),
        ["pair.copy"] = ("Copy link", "Копіювати"),
        ["pair.copied"] = ("Copied", "Скопійовано"),

        ["devices.title"] = ("CONNECTED DEVICES", "ПІДКЛЮЧЕНІ ПРИСТРОЇ"),
        ["devices.none"] = ("Waiting for a device", "Очікуємо на пристрій"),
        ["devices.some"] = ("Control your PC from the tablet", "Керуйте ПК з планшета"),

        ["mode.title"] = ("MODE", "РЕЖИМ"),
        ["mode.extended"] = ("Second monitor", "Другий монітор"),
        ["mode.remote"] = ("Touchpad", "Тачпад"),
        ["mode.extended.desc"] = ("The iPad is an extra display. Touch moves the cursor directly.", "iPad — додатковий екран. Дотик напряму керує курсором."),
        ["mode.remote.desc"] = ("The iPad mirrors your main display and acts as a trackpad.", "iPad дзеркалить основний екран і працює як тачпад."),
        ["mode.switching"] = ("Switching…", "Перемикання…"),
        ["mode.failed"] = ("Could not switch mode.", "Не вдалося змінити режим."),

        ["settings.title"] = ("STREAM", "ТРАНСЛЯЦІЯ"),
        ["settings.display"] = ("Display", "Екран"),
        ["settings.quality"] = ("Quality", "Якість"),
        ["display.main"] = ("Main display", "Основний екран"),
        ["display.second"] = ("Second monitor", "Другий монітор"),
        ["quality.low"] = ("Low · 540p · 20 fps", "Низька · 540p · 20 к/с"),
        ["quality.medium"] = ("Medium · 720p · 30 fps", "Середня · 720p · 30 к/с"),
        ["quality.high"] = ("High · 1080p · 30 fps", "Висока · 1080p · 30 к/с"),
        ["settings.startup"] = ("Launch with Windows", "Запускати разом із Windows"),

        ["diag.title"] = ("DIAGNOSTICS", "ДІАГНОСТИКА"),

        ["tray.open"] = ("Open", "Відкрити"),
        ["tray.exit"] = ("Exit", "Вийти"),
        ["tray.hint"] = ("DesktopUtility keeps running in the system tray.", "DesktopUtility продовжує працювати в треї."),

        ["error.title"] = ("DesktopUtility", "DesktopUtility"),
    };

    public static string Get(string key) =>
        Strings.TryGetValue(key, out var s) ? (Ukrainian ? s.Uk : s.En) : key;

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);
}

/// <summary>XAML access: <c>Text="{loc:T pair.title}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
