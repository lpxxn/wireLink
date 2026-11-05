using System.Reflection;

namespace WireLink.App;

public static class AppInfo
{
    public static string Product { get; } = ReadProduct();
    public static string Version { get; } = ReadVersion();
    public static string Company { get; } = ReadCompany();

    private static Assembly Assembly => Assembly.GetExecutingAssembly();

    private static string ReadProduct() =>
        Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product?.Trim()
        is { Length: > 0 } product
            ? product
            : "WireLink";

    private static string ReadCompany() =>
        Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company?.Trim()
        is { Length: > 0 } company
            ? company
            : "Bevone";

    private static string ReadVersion()
    {
        var informational = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var version = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (version.Length > 0)
                return version;
        }

        return Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
