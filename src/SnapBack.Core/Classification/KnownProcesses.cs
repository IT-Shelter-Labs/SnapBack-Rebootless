using System.Collections.Frozen;

namespace SnapBack.Core.Classification;

/// <summary>
/// Catalogue of well-known executables. All lookups are by file name and case-insensitive.
/// </summary>
/// <remarks>
/// Name-based protection deliberately applies regardless of the executable's location: a program
/// called <c>svchost.exe</c> is never closed, even if it lives somewhere unusual. Over-protecting is
/// always acceptable; under-protecting is not.
/// </remarks>
public static class KnownProcesses
{
    private static FrozenSet<string> Set(params string[] names) => names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pseudo processes reported by the kernel (no executable file).</summary>
    public static readonly FrozenSet<string> KernelPseudoProcesses = Set(
        "System", "Idle", "System Idle Process", "Registry", "Memory Compression", "Secure System", "vmmem", "vmmemWSL");

    /// <summary>
    /// Essential session, shell and security components. Protected wherever they run from.
    /// </summary>
    public static readonly FrozenSet<string> ShellAndSessionComponents = Set(
        // Core session
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe",
        "svchost.exe", "dwm.exe", "fontdrvhost.exe", "logonui.exe", "userinit.exe", "sihost.exe",
        "explorer.exe", "taskhostw.exe", "taskhost.exe", "ctfmon.exe", "runtimebroker.exe", "dllhost.exe",
        "conhost.exe", "openconsole.exe", "audiodg.exe", "spoolsv.exe", "wudfhost.exe", "dashost.exe",
        "wlanext.exe", "rdpclip.exe", "consent.exe", "werfault.exe", "wermgr.exe", "wmiprvse.exe",
        "unsecapp.exe", "aggregatorhost.exe", "backgroundtaskhost.exe", "compPkgSrv.exe",
        "mousocoreworker.exe", "tiworker.exe", "trustedinstaller.exe", "sgrmbroker.exe", "msiexec.exe",
        "taskmgr.exe", "smartscreen.exe",
        // Shell experience
        "startmenuexperiencehost.exe", "shellexperiencehost.exe", "shellhost.exe", "searchhost.exe",
        "searchapp.exe", "searchui.exe", "searchindexer.exe", "searchprotocolhost.exe", "searchfilterhost.exe",
        "textinputhost.exe", "applicationframehost.exe", "lockapp.exe", "widgets.exe", "widgetservice.exe",
        "widgetboard.exe", "systemsettingsbroker.exe", "useroobebroker.exe", "tabtip.exe",
        "inputapp.exe", "crossdeviceservice.exe",
        // Accessibility
        "osk.exe", "narrator.exe", "magnify.exe", "atbroker.exe",
        // Security
        "msmpeng.exe", "nissrv.exe", "mpdefendercoreservice.exe", "mpcmdrun.exe", "securityhealthsystray.exe",
        "securityhealthservice.exe", "securityhealthhost.exe", "msseces.exe",
        // Virtualization plumbing
        "vmcompute.exe", "vmwp.exe");

    /// <summary>
    /// WSL and Docker Desktop infrastructure. These are never closed as ordinary apps: WSL and
    /// Docker are stopped with their own commands (<c>wsl --shutdown</c>, <c>docker desktop stop</c>).
    /// </summary>
    public static readonly FrozenSet<string> ManagedEnvironmentProcesses = Set(
        // WSL
        "vmmem", "vmmemWSL", "wslservice.exe", "wslhost.exe", "wslrelay.exe",
        // Docker Desktop
        "docker desktop.exe", "vpnkit.exe", "vpnkit-bridge.exe", "docker-desktop.exe");

    /// <summary>Whether the process belongs to WSL or Docker Desktop (see <see cref="ManagedEnvironmentProcesses"/>).</summary>
    public static bool IsManagedEnvironment(string imageName) =>
        ManagedEnvironmentProcesses.Contains(imageName)
        || imageName.StartsWith("com.docker.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Package family name prefixes of Windows shell components that ship as packages outside
    /// the Windows directory.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedPackagePrefixes =
    [
        "MicrosoftWindows.Client.",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.Search",
        "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.Windows.SecHealthUI",
        "Microsoft.SecHealthUI",
        "Microsoft.LockApp",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.Windows.ContentDeliveryManager",
        "MicrosoftWindows.LKG.",
        "Microsoft.Windows.Ai.",
    ];

    /// <summary>
    /// User-facing tools that live in the Windows directory. They are not protected, but always
    /// need manual review.
    /// </summary>
    public static readonly FrozenSet<string> WindowsUserTools = Set(
        "notepad.exe", "mspaint.exe", "write.exe", "wordpad.exe", "charmap.exe", "mstsc.exe", "snippingtool.exe",
        "cmd.exe", "powershell.exe", "powershell_ise.exe", "ssh.exe", "wsl.exe", "calc.exe", "xpsrchvw.exe");

    /// <summary>Script interpreters, language runtimes and build tools.</summary>
    public static readonly FrozenSet<string> ScriptsAndRuntimes = Set(
        "python.exe", "pythonw.exe", "py.exe", "pyw.exe", "node.exe", "deno.exe", "bun.exe", "java.exe",
        "javaw.exe", "ruby.exe", "rubyw.exe", "perl.exe", "php.exe", "php-cgi.exe", "dotnet.exe", "rscript.exe",
        "r.exe", "julia.exe", "lua.exe", "luajit.exe", "go.exe", "cargo.exe", "rustc.exe", "msbuild.exe",
        "cl.exe", "link.exe", "git.exe", "make.exe", "mingw32-make.exe", "cmake.exe", "ninja.exe", "gradle.exe",
        "mvn.exe", "ffmpeg.exe", "pwsh.exe", "bash.exe", "sh.exe", "wscript.exe", "cscript.exe", "jupyter.exe",
        "uv.exe", "conda.exe");

    /// <summary>Shells. Sharing a console with one of them is acceptable when sending Ctrl+C.</summary>
    public static readonly FrozenSet<string> Shells = Set(
        "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe", "zsh.exe", "fish.exe", "nu.exe",
        "wsl.exe", "tcc.exe", "clink.exe");

    /// <summary>Terminal emulators: closing them ends every session inside.</summary>
    public static readonly FrozenSet<string> Terminals = Set(
        "windowsterminal.exe", "wt.exe", "alacritty.exe", "wezterm-gui.exe", "wezterm.exe", "mintty.exe",
        "conemu.exe", "conemu64.exe", "cmder.exe", "hyper.exe", "tabby.exe", "putty.exe", "kitty.exe",
        "mobaxterm.exe", "termius.exe", "warp.exe", "fluentterminal.exe");

    /// <summary>Editors, IDEs, office and creative software: may hold unsaved documents.</summary>
    public static readonly FrozenSet<string> DocumentEditors = Set(
        // Code editors & IDEs
        "code.exe", "code - insiders.exe", "cursor.exe", "windsurf.exe", "devenv.exe", "idea64.exe", "idea.exe",
        "pycharm64.exe", "pycharm.exe", "webstorm64.exe", "phpstorm64.exe", "clion64.exe", "rider64.exe",
        "goland64.exe", "rubymine64.exe", "datagrip64.exe", "rustrover64.exe", "fleet.exe", "studio64.exe",
        "sublime_text.exe", "notepad++.exe", "atom.exe", "zed.exe", "eclipse.exe", "netbeans64.exe",
        "qtcreator.exe", "gvim.exe", "nvim-qt.exe", "neovide.exe", "emacs.exe", "runemacs.exe", "notepad.exe",
        "ssms.exe", "dbeaver.exe", "unity.exe", "unrealeditor.exe", "godot.exe", "arduino ide.exe",
        // Office & notes
        "winword.exe", "excel.exe", "powerpnt.exe", "onenote.exe", "outlook.exe", "msaccess.exe", "visio.exe",
        "mspub.exe", "winproj.exe", "soffice.exe", "soffice.bin", "wps.exe", "et.exe", "wpp.exe", "obsidian.exe",
        "notion.exe", "typora.exe", "joplin.exe", "logseq.exe", "scrivener.exe", "texstudio.exe",
        // Creative
        "photoshop.exe", "illustrator.exe", "afterfx.exe", "adobe premiere pro.exe", "indesign.exe",
        "lightroom.exe", "acrobat.exe", "resolve.exe", "blender.exe", "krita.exe", "inkscape.exe", "gimp.exe",
        "gimp-2.10.exe", "gimp-3.0.exe", "audacity.exe", "reaper.exe", "flstudio.exe", "fl64.exe",
        "ableton live 12 suite.exe", "mspaint.exe", "paintdotnet.exe", "affinity photo 2.exe",
        "affinity designer 2.exe", "freecad.exe", "sketchup.exe", "acad.exe", "kicad.exe");

    /// <summary>Web browsers.</summary>
    public static readonly FrozenSet<string> Browsers = Set(
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe",
        "iexplore.exe", "librewolf.exe", "waterfox.exe", "thorium.exe", "browser.exe", "arc.exe", "zen.exe",
        "floorp.exe", "chromium.exe", "yandex.exe", "tor.exe");

    /// <summary>Sync, torrent and download clients.</summary>
    public static readonly FrozenSet<string> SyncAndTransfer = Set(
        "onedrive.exe", "dropbox.exe", "googledrivefs.exe", "megasync.exe", "iclouddrive.exe", "icloudservices.exe",
        "nextcloud.exe", "owncloud.exe", "yandexdisk2.exe", "box.exe", "syncthing.exe", "resilio-sync.exe",
        "qbittorrent.exe", "utorrent.exe", "bittorrent.exe", "transmission-qt.exe", "deluge.exe", "tixati.exe",
        "idman.exe", "fdm.exe", "jdownloader2.exe", "filezilla.exe", "winscp.exe");

    /// <summary>Game and application launchers.</summary>
    public static readonly FrozenSet<string> Launchers = Set(
        "steam.exe", "epicgameslauncher.exe", "battle.net.exe", "eadesktop.exe", "origin.exe", "galaxyclient.exe",
        "upc.exe", "ubisoftconnect.exe", "riotclientservices.exe", "rockstarlauncher.exe", "itch.exe",
        "jetbrains-toolbox.exe", "creative cloud.exe", "xboxpcapp.exe");

    /// <summary>Recording and streaming software.</summary>
    public static readonly FrozenSet<string> Recording = Set(
        "obs64.exe", "obs32.exe", "obs.exe", "bdcam.exe", "sharex.exe", "streamlabs obs.exe", "streamlabs.exe",
        "camtasia.exe", "fraps.exe", "nvidia share.exe");

    /// <summary>Virtual machines and container engines.</summary>
    public static readonly FrozenSet<string> VirtualMachines = Set(
        "virtualbox.exe", "virtualboxvm.exe", "vmware.exe", "vmware-vmx.exe", "vmplayer.exe", "vmconnect.exe",
        "qemu-system-x86_64.exe", "parallels.exe", "multipass.exe", "podman desktop.exe");

    /// <summary>
    /// Generic helper executables that belong to whichever application started them (embedded
    /// browsers, crash reporters, console hosts).
    /// </summary>
    public static readonly FrozenSet<string> HelperHosts = Set(
        "msedgewebview2.exe", "conhost.exe", "openconsole.exe", "crashpad_handler.exe", "chrome_crashpad_handler.exe",
        "cefsharp.browsersubprocess.exe", "qtwebengineprocess.exe", "crashreporter.exe", "werfault.exe");

    /// <summary>Heuristic: does the name look like an installer, updater or uninstaller?</summary>
    public static bool LooksLikeInstaller(string imageName)
    {
        var n = Path.GetFileNameWithoutExtension(imageName).ToLowerInvariant();
        return n.Contains("setup", StringComparison.Ordinal)
            || n.Contains("install", StringComparison.Ordinal)
            || n.Contains("update", StringComparison.Ordinal)
            || n.StartsWith("unins", StringComparison.Ordinal)
            || n.EndsWith("_patch", StringComparison.Ordinal);
    }

    /// <summary>Heuristic: does a window title advertise unsaved changes ("*file.txt", "● main.py")?</summary>
    public static bool TitleIndicatesUnsavedChanges(string title)
    {
        var t = title.Trim();
        if (t.Length < 2)
        {
            return false;
        }

        return t[0] is '*' or '●' or '•' or '⬤'
            || t.EndsWith('*')
            || t.Contains("* - ", StringComparison.Ordinal)
            || t.Contains("(unsaved)", StringComparison.OrdinalIgnoreCase)
            || t.Contains("[modified]", StringComparison.OrdinalIgnoreCase);
    }
}
