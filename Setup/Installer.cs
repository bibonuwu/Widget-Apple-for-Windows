using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinIslandSetup
{
    /// <summary>
    /// Установка «для текущего пользователя» — без прав администратора:
    /// файлы в %LOCALAPPDATA%\Programs\WinIsland, ярлыки, запись в «Приложения и возможности», автозапуск через HKCU\...\Run.
    /// </summary>
    internal static class Installer
    {
        public const string Name = "WinIsland";
        const string ExeName = "WinIsland.exe";
        const string UninstallerName = "Uninstall.exe";
        const string ExitSignalName = @"Local\WinIsland.Exit"; // то же имя слушает приложение (App.ExitSignalName)

        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinIsland";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string SettingsKey = @"Software\WinIsland";

        // приложению нужен .NET Desktop Runtime этой версии (как в WinIsland.csproj)
        public const int RuntimeMajor = 10;
        const string RuntimeUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";

        public static string Version
        {
            get
            {
                return Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            }
        }

        /// <summary>Уже установленная версия (null — не установлено).</summary>
        public static string InstalledVersion
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    return k?.GetValue("DisplayVersion") as string;
            }
        }

        public static string InstallDir
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                {
                    if (k?.GetValue("InstallLocation") is string dir && dir.Length > 0) return dir;
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
            }
        }

        public static string ExePath => Path.Combine(InstallDir, ExeName);
        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");
        static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");

        public static bool HasDesktopLink => File.Exists(DesktopLink);

        public static bool AutostartEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(Name) is string;
            }
        }

        // ===================================================================
        //  .NET Desktop Runtime
        // ===================================================================

        public static bool HasRuntime()
        {
            foreach (var root in DotnetRoots())
            {
                if (string.IsNullOrEmpty(root)) continue;
                string dir = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(dir)) continue;
                foreach (var d in Directory.GetDirectories(dir))
                {
                    string v = Path.GetFileName(d);
                    int dot = v.IndexOf('.');
                    if (dot > 0 && int.TryParse(v.Substring(0, dot), out int major) && major == RuntimeMajor) return true;
                }
            }
            return false;
        }

        static string[] DotnetRoots()
        {
            string reg = null;
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
                    reg = k?.GetValue("InstallLocation") as string;
            }
            catch { }
            string pf = Environment.GetEnvironmentVariable("ProgramW6432") ??
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return new[] { reg, Environment.GetEnvironmentVariable("DOTNET_ROOT"), Path.Combine(pf, "dotnet") };
        }

        /// <summary>Скачать .NET Desktop Runtime с сайта Microsoft и установить (Windows попросит подтверждение).</summary>
        public static async Task InstallRuntimeAsync(Action<double?, string> progress)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string file = Path.Combine(Path.GetTempPath(), $"windowsdesktop-runtime-{RuntimeMajor}-win-x64.exe");

            using (var wc = new WebClient())
            {
                wc.DownloadProgressChanged += (_, e) =>
                {
                    double? f = e.TotalBytesToReceive > 0 ? e.BytesReceived / (double)e.TotalBytesToReceive : (double?)null;
                    string mb = e.TotalBytesToReceive > 0
                        ? $"{e.BytesReceived / 1048576} из {e.TotalBytesToReceive / 1048576} МБ"
                        : $"{e.BytesReceived / 1048576} МБ";
                    progress(f, $"Скачиваю .NET Desktop Runtime {RuntimeMajor} · {mb}");
                };
                progress(0, $"Скачиваю .NET Desktop Runtime {RuntimeMajor}…");
                await wc.DownloadFileTaskAsync(new Uri(RuntimeUrl), file);
            }

            progress(null, "Устанавливаю .NET — подтвердите запрос Windows, если он появится");
            int code;
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(file, "/install /quiet /norestart") { UseShellExecute = true }))
                {
                    await Task.Run(() => p.WaitForExit());
                    code = p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new InstallException("Установка .NET отменена.");
            }
            finally
            {
                TryDelete(file);
            }

            if (!HasRuntime())
                throw new InstallException(code == 1602 || code == 1223
                    ? "Установка .NET отменена."
                    : $"Не удалось установить .NET Desktop Runtime (код {code}).");
        }

        // ===================================================================
        //  Установка
        // ===================================================================

        public static void Install(bool desktopLink, Action<double?, string> progress)
        {
            string dir = InstallDir;

            progress(null, "Закрываю WinIsland, если он запущен…");
            CloseRunningApp();

            progress(0, "Копирую файлы…");
            Directory.CreateDirectory(dir);
            Extract(dir, f => progress(0.9 * f, "Копирую файлы…"));
            string exe = Path.Combine(dir, ExeName);
            if (!File.Exists(exe)) throw new InstallException("Установщик повреждён: внутри нет WinIsland.exe.");

            // копия установщика — это и есть деинсталлятор
            string self = Process.GetCurrentProcess().MainModule.FileName;
            string uninstaller = Path.Combine(dir, UninstallerName);
            if (!SamePath(self, uninstaller)) File.Copy(self, uninstaller, true);

            progress(0.95, "Создаю ярлыки…");
            const string about = "Динамический остров для Windows";
            Shortcut.Create(StartMenuLink, exe, about);
            if (desktopLink) Shortcut.Create(DesktopLink, exe, about);
            else TryDelete(DesktopLink);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Name);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long kb = (new FileInfo(exe).Length + new FileInfo(uninstaller).Length) / 1024;
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
            }

            progress(1, "Готово");
        }

        public static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(Name, $"\"{ExePath}\"");
                else k.DeleteValue(Name, false);
            }
        }

        public static void Launch()
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = InstallDir });
        }

        // ===================================================================
        //  Удаление
        // ===================================================================

        /// <summary>Удаляет всё, кроме самого себя. Возвращает папку, которую нужно стереть после выхода (или null).</summary>
        public static string Uninstall(Action<double?, string> progress)
        {
            string dir = InstallDir;

            progress(null, "Закрываю WinIsland…");
            CloseRunningApp();

            progress(0.3, "Убираю автозапуск и ярлыки…");
            try { SetAutostart(false); } catch { }
            TryDelete(StartMenuLink);
            TryDelete(DesktopLink);

            progress(0.6, "Удаляю файлы и настройки…");
            try { Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
                if (Directory.Exists(data)) Directory.Delete(data, true);
            }
            catch { }

            string self = Process.GetCurrentProcess().MainModule.FileName;
            bool selfInside = false;
            if (IsOurDir(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (SamePath(f, self)) { selfInside = true; continue; }
                    TryDelete(f);
                }
                if (!selfInside) { try { Directory.Delete(dir, true); } catch { } }
            }

            progress(1, "Готово");
            return selfInside ? dir : null;
        }

        /// <summary>Стереть папку (вместе с Uninstall.exe) через пару секунд после выхода.</summary>
        public static void DeleteAfterExit(string dir)
        {
            if (!IsOurDir(dir)) return;
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rd /s /q \"{dir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath() // не внутри удаляемой папки
                });
            }
            catch { }
        }

        // ===================================================================
        //  Вспомогательное
        // ===================================================================

        /// <summary>Распаковать вшитый payload.zip (собранный WinIsland) в папку установки.</summary>
        static void Extract(string dir, Action<double> progress)
        {
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (src == null) throw new InstallException("Установщик повреждён: внутри нет файлов программы.");
                using (var zip = new ZipArchive(src, ZipArchiveMode.Read))
                {
                    long total = 1, done = 0;
                    foreach (var e in zip.Entries) total += e.Length;
                    var buf = new byte[81920];
                    int lastPct = -1;
                    foreach (var e in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue; // это папка
                        string target = Path.GetFullPath(Path.Combine(root, e.FullName));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string tmp = target + ".new";
                        using (var s = e.Open())
                        using (var d = File.Create(tmp))
                        {
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            {
                                d.Write(buf, 0, n);
                                done += n;
                                int pct = (int)(100 * done / total);
                                if (pct != lastPct) { lastPct = pct; progress(done / (double)total); }
                            }
                        }
                        Replace(tmp, target);
                    }
                }
            }
        }

        /// <summary>Страховка: удаляем только папку «…\WinIsland», в которой лежит наша программа.</summary>
        static bool IsOurDir(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(Path.GetFileName(full), Name, StringComparison.OrdinalIgnoreCase) &&
                   (File.Exists(Path.Combine(full, ExeName)) || File.Exists(Path.Combine(full, UninstallerName)));
        }

        static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>Просит запущенный WinIsland закрыться (он слушает именованное событие), иначе завершает процесс.</summary>
        static void CloseRunningApp()
        {
            bool asked = false;
            try
            {
                if (EventWaitHandle.TryOpenExisting(ExitSignalName, out var ev))
                    using (ev) { ev.Set(); asked = true; }
            }
            catch { }

            foreach (var p in Process.GetProcessesByName("WinIsland"))
            {
                try
                {
                    if (!p.WaitForExit(asked ? 5000 : 300))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        static void Replace(string tmp, string target)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                    return;
                }
                catch (Exception) when (i < 10)
                {
                    Thread.Sleep(300); // файл ещё держит только что закрытый процесс
                }
                catch (Exception ex)
                {
                    TryDelete(tmp);
                    throw new InstallException(
                        $"Не получилось заменить {Path.GetFileName(target)} — закройте WinIsland и попробуйте снова. {ex.Message}");
                }
            }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    internal sealed class InstallException : Exception
    {
        public InstallException(string message) : base(message) { }
    }

    /// <summary>Ярлык .lnk через WScript.Shell (позднее связывание, без лишних зависимостей).</summary>
    internal static class Shortcut
    {
        public static void Create(string lnkPath, string target, string description)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object lnk = null;
            try
            {
                lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                var t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { description });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally
            {
                if (lnk != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
