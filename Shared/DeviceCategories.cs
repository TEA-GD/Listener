namespace WatcherShared;

internal static class DeviceCategories
{
    internal static readonly string[] Values = ["linux_servers", "windows_servers", "user_pcs", "project_stands"];
    internal static readonly string[] Labels = ["Linux servers", "Windows servers", "User PCs", "Project Stands"];
    internal static string Validate(string? value)
    {
        if (value == "legacy" || Values.Contains(value)) return value!;
        throw new ArgumentException("Invalid device category. Choose linux_servers, windows_servers, user_pcs or project_stands.");
    }
}
