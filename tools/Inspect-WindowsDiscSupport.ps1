[CmdletBinding()]
param([switch]$AsJson, [string]$ReportPath)

$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 8) { throw 'Run this probe in 64-bit PowerShell, matching NOIR.' }

# Activation only: no disc is opened, no graph runs, and no settings change.
# The GUIDs come from Windows SDK uuids.h, strmif.h and wmcodecdsp.h.
if (-not ('NoirDiscComponentProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class NoirDiscComponentProbe
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    public static int Activate(string classId, string interfaceId)
    {
        int initialized = CoInitializeEx(IntPtr.Zero, 0);
        // PowerShell may already own an STA. Use that apartment without uninitializing it.
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) return initialized;
        IntPtr instance = IntPtr.Zero;
        try
        {
            Guid clsid = new Guid(classId), iid = new Guid(interfaceId);
            return CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out instance);
        }
        finally
        {
            if (instance != IntPtr.Zero) Marshal.Release(instance);
            if (initialized >= 0) CoUninitialize();
        }
    }
}
'@
}

$components = @(
    @{ Name='Windows DVD Graph Builder'; ClassId='FCC152B7-F372-11D0-8E00-00C04FD7C08B'; InterfaceId='FCC152B6-F372-11D0-8E00-00C04FD7C08B' },
    @{ Name='Windows DVD navigation controls'; ClassId='9B8C4620-2C1A-11D0-8493-00A02438AD48'; InterfaceId='33BC7430-EEC0-11D2-8201-00A0C9D74842' },
    @{ Name='Windows DVD state information'; ClassId='9B8C4620-2C1A-11D0-8493-00A02438AD48'; InterfaceId='34151510-EEC0-11D2-8201-00A0C9D74842' },
    @{ Name='Microsoft DVD video decoder'; ClassId='212690FB-83E5-4526-8FD7-74478B7939CD'; InterfaceId='56A86895-0AD4-11CE-B03A-0020AF0BA770' },
    @{ Name='Microsoft DVD audio decoder'; ClassId='E1F1A0B8-BEEE-490D-BA7C-066C40B5E2B9'; InterfaceId='56A86895-0AD4-11CE-B03A-0020AF0BA770' }
)

$results = foreach ($component in $components) {
    $result = [NoirDiscComponentProbe]::Activate($component.ClassId, $component.InterfaceId)
    [PSCustomObject]@{
        Component = $component.Name
        Activates = $result -ge 0
        HResult = '0x' + $result.ToString('X8')
    }
}
$optionalCodecs = foreach ($codec in @(
    @{ Name='Media Foundation MPEG-2'; ClassId='2D709E52-123F-49B5-9CBC-9AF5CDE28FB9' },
    @{ Name='Media Foundation H.264'; ClassId='62CE7E72-4C71-4D20-B15D-452831A87D9D' }
)) {
    $result = [NoirDiscComponentProbe]::Activate($codec.ClassId, 'BF94C121-5B05-4E6F-8000-BA598961414D')
    [PSCustomObject]@{
        Component = $codec.Name
        ClassId = $codec.ClassId
        Activates = $result -ge 0
        HResult = '0x' + $result.ToString('X8')
    }
}
$report = [PSCustomObject]@{
    ProbeVersion = 2
    Components = @($results)
    OptionalMediaFoundationCodecs = @($optionalCodecs)
    DvdPlaybackVerified = $false
    BluRayPlaybackVerified = $false
    Limitation = 'Component activation does not verify decoding, menus, protection, audio/subtitle selection, or real-disc playback. Optional Media Foundation codecs are separate from the DirectShow DVD/CSS path; their absence alone does not prove that path is unavailable. VLC/AACS must remain until their replacements are verified.'
}
if ($ReportPath) {
    # A caller-selected new diagnostic file only; never replace an existing report.
    $reportBytes = [Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 5))
    $reportStream = [IO.File]::Open([IO.Path]::GetFullPath($ReportPath), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $reportStream.Write($reportBytes, 0, $reportBytes.Length) }
    finally { $reportStream.Dispose() }
}
if ($AsJson) { $report | ConvertTo-Json -Depth 4 }
else {
    $results | Format-Table -AutoSize
    $optionalCodecs | Format-Table -AutoSize
    Write-Output $report.Limitation
}
