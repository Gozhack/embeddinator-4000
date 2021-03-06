using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CppSharp;
using CppSharp.Generators;

namespace MonoEmbeddinator4000
{
    public enum BitCodeMode
    {
        None = 0,
        ASMOnly = 1,
        LLVMOnly = 2,
        MarkerOnly = 3,
    }

    [Flags]
    public enum Abi
    {
        None   =   0,
        i386   =   1,
        ARMv6  =   2,
        ARMv7  =   4,
        ARMv7s =   8,
        ARM64 =   16,
        x86_64 =  32,
        Thumb  =  64,
        LLVM   = 128,
        ARMv7k = 256,
        SimulatorArchMask = i386 | x86_64,
        DeviceArchMask = ARMv6 | ARMv7 | ARMv7s | ARMv7k | ARM64,
        ArchMask = SimulatorArchMask | DeviceArchMask,
        Arch64Mask = x86_64 | ARM64,
        Arch32Mask = i386 | ARMv6 | ARMv7 | ARMv7s | ARMv7k,
    }

    public static class AbiExtensions 
    {
        public static string AsString (this Abi self)
        {
            var rv = (self & Abi.ArchMask).ToString ();
            if ((self & Abi.LLVM) == Abi.LLVM)
                rv += "+LLVM";
            if ((self & Abi.Thumb) == Abi.Thumb)
                rv += "+Thumb";
            return rv;
        }

        public static string AsArchString (this Abi self)
        {
            return (self & Abi.ArchMask).ToString ().ToLowerInvariant ();
        }
    }

    public class Application
    {
        public bool EnableDebug;
        public bool PackageMdb;
        public bool EnableLLVMOnlyBitCode;
        public bool EnableMSym;

        public bool UseDlsym(string aname) { return false; }
    }

    public partial class Driver
    {
        public static string GetAppleTargetFrameworkIdentifier(TargetPlatform platform)
        {
            switch (platform) {
            case TargetPlatform.MacOS:
                return "Xamarin.Mac";
            case TargetPlatform.iOS:
                return "Xamarin.iOS";
            case TargetPlatform.WatchOS:
                return "Xamarin.WatchOS";
            case TargetPlatform.TVOS:
                return "Xamarin.TVOS";
            }

            throw new InvalidOperationException ("Unknown Apple target platform: " + platform);
        }

        public static string GetAppleAotCompiler(TargetPlatform platform, string cross_prefix, bool is64bits)
        {
            switch (platform) {
            case TargetPlatform.iOS:
                if (is64bits) {
                    return Path.Combine (cross_prefix, "bin", "arm64-darwin-mono-sgen");
                } else {
                    return Path.Combine (cross_prefix, "bin", "arm-darwin-mono-sgen");
                }
            case TargetPlatform.WatchOS:
                return Path.Combine (cross_prefix, "bin", "armv7k-unknown-darwin-mono-sgen");
            case TargetPlatform.TVOS:
                return Path.Combine (cross_prefix, "bin", "aarch64-unknown-darwin-mono-sgen");
            }

            throw new InvalidOperationException ("Unknown Apple target platform: " + platform);
        }

        public static string Quote (string f)
        {
            if (f.IndexOf (' ') == -1 && f.IndexOf ('\'') == -1 && f.IndexOf (',') == -1)
                return f;

            var s = new StringBuilder ();

            s.Append ('"');
            foreach (var c in f) {
                if (c == '"' || c == '\\')
                    s.Append ('\\');

                s.Append (c);
            }
            s.Append ('"');

            return s.ToString ();
        }

        public static string GetAotArguments (Application app, string filename, Abi abi,
            string outputDir, string outputFile, string llvmOutputFile, string dataFile)
        {
            string aot_args = string.Empty;
            string aot_other_args = string.Empty;
            bool debug_all = false;
            var debug_assemblies = new List<string>();

            string fname = Path.GetFileName (filename);
            var args = new StringBuilder ();
            bool enable_llvm = (abi & Abi.LLVM) != 0;
            bool enable_thumb = (abi & Abi.Thumb) != 0;
            bool enable_debug = app.EnableDebug;
            bool enable_mdb = app.PackageMdb;
            bool llvm_only = app.EnableLLVMOnlyBitCode;
            string arch = abi.AsArchString ();

            args.Append ("--debug ");

            if (enable_llvm)
                args.Append ("--llvm ");

            if (!llvm_only)
                args.Append ("-O=gsharedvt ");
            args.Append (aot_other_args).Append (" ");
            args.Append ("--aot=mtriple=");
            args.Append (enable_thumb ? arch.Replace ("arm", "thumb") : arch);
            args.Append ("-ios,");
            args.Append ("data-outfile=").Append (Quote (dataFile)).Append (",");
            args.Append (aot_args);
            if (llvm_only)
                args.Append ("llvmonly,");
            else
                args.Append ("full,");

            var aname = Path.GetFileNameWithoutExtension (fname);
            //var sdk_or_product = Profile.IsSdkAssembly (aname) || Profile.IsProductAssembly (aname);
            var sdk_or_product = false;

            if (enable_llvm)
                args.Append ("nodebug,");
            else if (!(enable_debug || enable_mdb))
                args.Append ("nodebug,");
            else if (debug_all || debug_assemblies.Contains (fname) || !sdk_or_product)
                args.Append ("soft-debug,");

            args.Append ("dwarfdebug,");

            /* Needed for #4587 */
            if (enable_debug && !enable_llvm)
                args.Append ("no-direct-calls,");

            if (!app.UseDlsym (filename))
                args.Append ("direct-pinvoke,");

            if (app.EnableMSym) {
                var msymdir = Quote (Path.Combine (outputDir, "Msym"));
                args.Append ($"msym-dir={msymdir},");
            }

            //if (enable_llvm)
                //args.Append ("llvm-path=").Append (MonoTouchDirectory).Append ("/LLVM/bin/,");

            if (!llvm_only)
                args.Append ("outfile=").Append (Quote (outputFile));
            if (!llvm_only && enable_llvm)
                args.Append (",");
            if (enable_llvm)
                args.Append ("llvm-outfile=").Append (Quote (llvmOutputFile));
            args.Append (" \"").Append (filename).Append ("\"");
            return args.ToString ();
        }    

        void InvokeCompiler(string compiler, string arguments, Dictionary<string, string> envVars = null)
        {
            Diagnostics.Debug("Invoking: {0} {1}", compiler, arguments);

            var process = new Process();
            process.StartInfo.FileName = compiler;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            if (envVars != null)
                foreach (var kvp in envVars)
                    process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            process.OutputDataReceived += (sender, args) => Diagnostics.Message("{0}", args.Data);
            Diagnostics.PushIndent();
            process.Start();
            process.BeginOutputReadLine();
            process.WaitForExit();
            Diagnostics.PopIndent();
        }

        private IEnumerable<string> GetOutputFiles(string pattern)
        {
            return Directory.EnumerateFiles(Options.OutputDir)
                    .Where(file => file.EndsWith(pattern, StringComparison.OrdinalIgnoreCase));
        }

        void AotAssemblies()
        {
            switch (Options.Platform)
            {
            case TargetPlatform.iOS:
            case TargetPlatform.TVOS:
            case TargetPlatform.WatchOS:
			{
				var detectAppleSdks = new Xamarin.iOS.Tasks.DetectIPhoneSdks()
				{
					TargetFrameworkIdentifier = GetAppleTargetFrameworkIdentifier(Options.Platform)
				};

				if (!detectAppleSdks.Execute())
					throw new Exception("Error detecting Xamarin.iOS SDK.");

                var monoTouchSdk = Xamarin.iOS.Tasks.IPhoneSdks.MonoTouch;
                if (monoTouchSdk.ExtendedVersion.Version.Major < 10)
                    throw new Exception("Unsupported Xamarin.iOS version, upgrade to 10 or newer.");

				string aotCompiler = GetAppleAotCompiler(Options.Platform,
					detectAppleSdks.XamarinSdkRoot, is64bits: false);

				var app = new Application();

				// Call the Mono AOT cross compiler for all input assemblies.
				foreach (var assembly in Options.Project.Assemblies)
				{
					var args = GetAotArguments(app, assembly, Abi.ARMv7, Path.GetFullPath(Options.OutputDir),
						assembly + ".o", assembly + ".llvm.o", assembly + ".data");

					Console.WriteLine("{0} {1}", aotCompiler, args);
				}
				break;
			}
            case TargetPlatform.Windows:
            case TargetPlatform.Android:
                throw new NotSupportedException(string.Format(
                    "AOT cross compilation to target platform '{0}' is not supported.",
                    Options.Platform));
            case TargetPlatform.MacOS:
                break;
            }
        }

        void CompileCode()
        {
            var files = GetOutputFiles("c");

            switch (Options.Language)
            {
            case GeneratorKind.ObjectiveC:
                files = files.Concat(GetOutputFiles("mm"));
                break;
            case GeneratorKind.CPlusPlus:
                files = files.Concat(GetOutputFiles("cpp"));
                break;
            }

            const string exportDefine = "MONO_M2N_DLL_EXPORT";

            if (Platform.IsWindows)
            {
                List<ToolchainVersion> vsSdks;
                MSVCToolchain.GetVisualStudioSdks(out vsSdks);

                if (vsSdks.Count == 0)
                    throw new Exception("Visual Studio SDK was not found on your system.");

                var vsSdk = vsSdks.FirstOrDefault();
                var clBin = Path.GetFullPath(
                    Path.Combine(vsSdk.Directory, "..", "..", "VC", "bin", "cl.exe"));

                var monoPath = ManagedToolchain.FindMonoPath();
                var output = Options.LibraryName ??
                    Path.GetFileNameWithoutExtension(Options.Project.Assemblies[0]);
                output = Path.Combine(Options.OutputDir, output);
                var invocation = string.Format(
                    "/nologo /D{0} -I\"{1}\\include\\mono-2.0\" {2} \"{1}\\lib\\monosgen-2.0.lib\" {3} {4}",
                    exportDefine, monoPath, string.Join(" ", files.ToList()),
                    Options.CompileSharedLibrary ? "/LD" : string.Empty,
                    output);

                var vsVersion = (VisualStudioVersion)(int)vsSdk.Version;
                var includes = MSVCToolchain.GetSystemIncludes(vsVersion);

                Dictionary<string, string> envVars = null;
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INCLUDE")))
                {
                    envVars = new Dictionary<string, string>();
                    envVars["INCLUDE"] = string.Join(";", includes);

                    var clLib = Path.GetFullPath(
                        Path.Combine(vsSdk.Directory, "..", "..", "VC", "lib"));
                    envVars["LIB"] = clLib;
                }

                InvokeCompiler(clBin, invocation, envVars);

                return;
            }
            else if (Platform.IsMacOS)
            {
                switch (Options.Platform)
                {
                case TargetPlatform.iOS:
                case TargetPlatform.TVOS:
                case TargetPlatform.WatchOS:
                    AotAssemblies();
                    break;
                case TargetPlatform.Windows:
                case TargetPlatform.Android:
                    throw new NotSupportedException(string.Format(
                        "Cross compilation to target platform '{0}' is not supported.",
                        Options.Platform));
                case TargetPlatform.MacOS:
                    var xcodePath = XcodeToolchain.GetXcodeToolchainPath();
                    var clangBin = Path.Combine(xcodePath, "usr/bin/clang");
                    var monoPath = ManagedToolchain.FindMonoPath();
    
                    var invocation = string.Format(
                        "-D{0} -framework CoreFoundation -I\"{1}/include/mono-2.0\" " +
                        "-L\"{1}/lib/\" -lmonosgen-2.0 {2}",
                        exportDefine, monoPath, string.Join(" ", files.ToList()));
    
                    InvokeCompiler(clangBin, invocation);
                    break;
                }
                return;
            }

            throw new NotImplementedException();
        }
    }
}
