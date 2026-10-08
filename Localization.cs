using System.Globalization;

namespace VpnProvisioner;

public static class Localization
{
    public static string Language { get; private set; } = "ru";

    public static void SetLanguage(string lang)
    {
        Language = lang;
    }

    public static void DetectSystemLanguage()
    {
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Language = culture == "ru" ? "ru" : "en";
    }

    public static string T(string key)
    {
        return Language == "ru" ? Ru(key) : En(key);
    }

    private static string Ru(string key) => key switch
    {
        "server_connection" => "Подключение к серверу",
        "host_placeholder" => "IP-адрес или домен сервера",
        "ssh_user_placeholder" => "Пользователь SSH",
        "password_placeholder" => "Пароль сервера",
        "key_placeholder" => "Путь к приватному SSH-ключу (опционально)",
        "browse" => "Обзор",
        "key_pass_placeholder" => "Парольная фраза ключа (опционально)",
        "protocol_options" => "Протокол и параметры",
        "protocol" => "Протокол",
        "sni_label" => "SNI (кроме AmneziaWG)",
        "force_label" => "Пересоздать, если уже установлен",
        "install" => "Установить",
        "progress" => "Ход работы",
        "result_link" => "Готовая ссылка",
        "copy" => "Копировать",
        "copied" => "Скопировано",
        "switch_theme" => "Сменить тему",
        "switch_language" => "Сменить язык",
        "err_host" => "Укажите адрес сервера.",
        "err_port" => "Некорректный порт SSH.",
        "err_user" => "Укажите пользователя SSH.",
        "err_auth" => "Укажите пароль сервера или путь к приватному SSH-ключу.",
        "err_key_file" => "Укажите существующий файл приватного ключа.",
        "err_protocol" => "Выберите протокол установки.",
        "done" => "Готово! Ссылка ниже — нажмите «Копировать».",
        "error" => "ОШИБКА: ",
        "unexpected_error" => "Непредвиденная ошибка: ",
        _ => key
    };

    private static string En(string key) => key switch
    {
        "server_connection" => "Server Connection",
        "host_placeholder" => "Server IP or domain",
        "ssh_user_placeholder" => "SSH User",
        "password_placeholder" => "Server Password",
        "key_placeholder" => "Private SSH key path (optional)",
        "browse" => "Browse",
        "key_pass_placeholder" => "Key passphrase (optional)",
        "protocol_options" => "Protocol & Options",
        "protocol" => "Protocol",
        "sni_label" => "SNI (except AmneziaWG)",
        "force_label" => "Force reconfigure if already installed",
        "install" => "Install",
        "progress" => "Progress",
        "result_link" => "Result Link",
        "copy" => "Copy",
        "copied" => "Copied",
        "switch_theme" => "Switch theme",
        "switch_language" => "Switch language",
        "err_host" => "Server address is required.",
        "err_port" => "Invalid SSH port.",
        "err_user" => "SSH user is required.",
        "err_auth" => "Provide a server password or path to a private SSH key.",
        "err_key_file" => "Specified key file does not exist.",
        "err_protocol" => "Select an installation protocol.",
        "done" => "Done! Use the link below — click \"Copy\".",
        "error" => "ERROR: ",
        "unexpected_error" => "Unexpected error: ",
        _ => key
    };
}
