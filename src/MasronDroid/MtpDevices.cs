using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MasronDroid;

internal static class MtpDevices
{
    private const string PortableDeviceClass = "{eec5ad98-8080-425f-922a-dabf3de3f69a}";
    private const uint PresentClass = 0x100 | 0x200;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint size, uint flags);

    public static bool IsOdinPresent()
    {
        const uint presentUsbEnumerator = 0x100 | 0x1;
        if (CM_Get_Device_ID_List_SizeW(out uint size, "USB", presentUsbEnumerator) != 0 || size <= 1) return false;
        var buffer = new char[size];
        if (CM_Get_Device_ID_ListW("USB", buffer, size, presentUsbEnumerator) != 0) return false;
        return new string(buffer).Split('\0').Any(id =>
            id.StartsWith(@"USB\VID_04E8&PID_685D\", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPresent()
    {
        if (CM_Get_Device_ID_List_SizeW(out uint size, PortableDeviceClass, PresentClass) != 0 || size <= 1) return false;
        var buffer = new char[size];
        if (CM_Get_Device_ID_ListW(PortableDeviceClass, buffer, size, PresentClass) != 0) return false;
        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id);
                foreach (var name in new[] { "CompatibleIDs", "HardwareID", "DeviceDesc" })
                {
                    var value = key?.GetValue(name);
                    if (value is string description && description.Contains("MTP", StringComparison.OrdinalIgnoreCase)
                        || value is string[] ids && ids.Any(x => x.Contains("MTP", StringComparison.OrdinalIgnoreCase))) return true;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }
        }
        return false;
    }
}
