// Copyright 2018 Yassine Riahi and Liam Flookes. Provided under a MIT License, see license file on github.
// Used to generate a fastbuild .bff file from UnrealBuildTool to allow caching and distributed builds.
// Tested with Windows 10, Visual Studio 2015/2017, Unreal Engine 4.18, FastBuild v0.95
// Durango is fully supported (Compiles with VS2015).
// Orbis will likely require some changes.
// TODO (PQU) - fork
// We should fork this integration as we diverged a lot, and original work this is based upon is not exactly a masterpiece ...
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using Tools.DotNETCommon;
namespace UnrealBuildTool
{
	class FASTBuild : ActionExecutor
	{
		// Used to specify a non-standard location for the FBuild.exe, for example if you have not added it to your PATH environment variable.
		public static string FBuildExePathOverride = Path.Combine(UnrealBuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()), "Binaries", "ThirdParty", "FastBuild", "Windows", "fbuild.exe");

		// Controls network build distribution
		private bool bEnableDistribution = true;

		// Controls whether to use caching at all. CachePath and CacheMode are only relevant if this is enabled.
		private bool bEnableCaching = true;

		// Location of the shared cache, it could be a local or network path (i.e: @"\\DESKTOP-BEAST\FASTBuildCache").
		// Only relevant if bEnableCaching is true;
		private static string CachePath = @"\\fastbuild\fastbuild\cache";

		// Location of the broker path used for distribution workers discovery
		// Only relevant if bEnableDistribution is true;
		private static string BrokerPath = @"\\fastbuild\\fastbuild\\tokens";

		public enum eCacheMode
		{
			ReadWrite, // This machine will both read and write to the cache
			ReadOnly,  // This machine will only read from the cache, use for developer machines when you have centralized build machines
			WriteOnly, // This machine will only write from the cache, use for build machines when you have centralized build machines
		}

		// Cache access mode
		// Only relevant if bEnableCaching is true;
		private eCacheMode CacheMode = eCacheMode.ReadWrite;

		public FASTBuild()
		{
			XmlConfig.ApplyTo(this);
		}

		public override string Name
		{
			get { return "FASTBuild"; }
		}

		public static bool IsAvailable()
		{
			if (FBuildExePathOverride != "")
			{
				// (SHO) -- on Linux, fbuild.exe should not be used
				if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Linux)
				{
					FBuildExePathOverride = "/usr/local/bin/fbuild";
				}
				return File.Exists(FBuildExePathOverride);
			}

			// Get the name of the FASTBuild executable.
			string fbuild = "fbuild";
			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win64)
			{
				fbuild = "fbuild.exe";
			}

			// Search the path for it
			string PathVariable = Environment.GetEnvironmentVariable("PATH");
			foreach (string SearchPath in PathVariable.Split(Path.PathSeparator))
			{
				try
				{
					string PotentialPath = Path.Combine(SearchPath, fbuild);
					if (File.Exists(PotentialPath))
					{
						return true;
					}
				}
				catch (ArgumentException)
				{
					// PATH variable may contain illegal characters; just ignore them.
				}
			}
			return false;
		}

		private enum FBBuildType
		{
			Windows,
			XBOne,
			PS4
		}

		private FBBuildType BuildType = FBBuildType.Windows;

		private void DetectBuildType(List<Action> Actions)
		{
			// TODO (SHO) refactor this
			var CppActions = Actions.Where(a => a.ActionType == ActionType.Compile || a.ActionType == ActionType.Link);

			if (CppActions.Any(a => a.CommandArguments.Contains(@"Intermediate\Build\PS4")))
			{
				BuildType = FBBuildType.PS4;
			}
			else if (CppActions.Any(a => a.CommandArguments.Contains(@"Intermediate\Build\XboxOne")))
			{
				BuildType = FBBuildType.XBOne;
			}
			else if (CppActions.Any(a => a.CommandArguments.Contains(@"Intermediate\Build\Win64")))
			{
				BuildType = FBBuildType.Windows;
			}
		}

		static readonly string DurangoXDK;
		static readonly string SCE_ORBIS_SDK_DIR;
		static readonly string DXSDK_DIR;
		static readonly string CommonProgramFiles;

		static string GetSDKEnvironmentVariable(string key, bool needed = false)
		{
			string Value = System.Environment.GetEnvironmentVariable(key);
			if (string.IsNullOrEmpty(Value))
			{
				if (needed)
					Log.TraceError($"Failed to find needed environment variable '{key}', did you install all SDKs ?");
				else
					Log.TraceWarning($"Failed to find optional environment variable '{key}', did you install all SDKs ?");

				Value = string.Empty;
			}
			Value = Value.Trim('/').Trim('\\');
			return Value;
		}

		static FASTBuild()
		{
			DurangoXDK = GetSDKEnvironmentVariable("DurangoXDK");
			SCE_ORBIS_SDK_DIR = GetSDKEnvironmentVariable("SCE_ORBIS_SDK_DIR");
			DXSDK_DIR = GetSDKEnvironmentVariable("DXSDK_DIR");
			CommonProgramFiles = GetSDKEnvironmentVariable("CommonProgramFiles", true);
		}

		private bool IsMSVC() { return BuildType == FBBuildType.Windows || BuildType == FBBuildType.XBOne; }
		private bool IsSymbolTool(Action action) { return action.CommandPath.Contains("XboxOnePDBFileUtil.exe")
		                                               || action.CommandPath.Contains("PS4SymbolTool.exe"); }

		private string GetCompilerName()
		{
			switch (BuildType)
			{
				default:
				case FBBuildType.XBOne:
				case FBBuildType.Windows: return "UE4Compiler";
				case FBBuildType.PS4: return "UE4PS4Compiler";
			}
		}

		//Run FASTBuild on the list of actions. Relies on fbuild.exe being in the path.
		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			bool FASTBuildResult = true;
			if (Actions.Count > 0)
			{
				DetectBuildType(Actions);

				string FASTBuildFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate", "Build", "fbuild.bff");
				if (CreateBffFile(Actions, FASTBuildFilePath))
				{
					return ExecuteBffFile(FASTBuildFilePath);
				}
				else
				{
					return false;
				}
			}
			return FASTBuildResult;
		}

		private void AddText(string StringToWrite)
		{
			byte[] Info = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite.Replace("\r\n", "\n"));
			bffOutputFileStream.Write(Info, 0, Info.Length);
		}


		private string SubstituteEnvironmentVariables(string commandLineString)
		{
			// (PQU) - Inline environment vars inside BFF, it would never be reused anyway ... and this is messing with Exec() and response files
#if false
			string outputString = commandLineString.Replace("$(DurangoXDK)", "$DurangoXDK$");
			outputString = outputString.Replace("$(SCE_ORBIS_SDK_DIR)", "$SCE_ORBIS_SDK_DIR$");
			outputString = outputString.Replace("$(DXSDK_DIR)", "$DXSDK_DIR$");
			outputString = outputString.Replace("$(CommonProgramFiles)", "$CommonProgramFiles$");
#else
			string outputString = commandLineString;
			outputString = outputString.Replace("$(DurangoXDK)", DurangoXDK);
			outputString = outputString.Replace("$(SCE_ORBIS_SDK_DIR)", SCE_ORBIS_SDK_DIR);
			outputString = outputString.Replace("$(DXSDK_DIR)", DXSDK_DIR);
			outputString = outputString.Replace("$(CommonProgramFiles)", CommonProgramFiles);
#endif

			return outputString;
		}

		// (PQU) - Stronger input file parsing
		//private Dictionary<string, string> ParseCommandLineOptions(string CompilerCommandLine, string[] SpecialOptions, bool SaveResponseFile = false)

		struct FInputFile
		{
			public readonly int? ActionIndex;
			public readonly string FileName;

			public FInputFile(string fileName)
			{
				ActionIndex = null;
				FileName = fileName;
			}

			public FInputFile(int actionIndex, string fileName)
			{
				ActionIndex = actionIndex;
				FileName = fileName;
			}

			public override string ToString()
			{
				return (ActionIndex.HasValue ? $"Action_{ActionIndex}" : FileName);
			}

			public bool AllowCaching
			{
				get
				{
					return (FileName.EndsWith(".gen.cpp") || // generated files are always cached
							Path.GetFileName(FileName).StartsWith(Unity.ModulePrefix) || // unity files are also cached
							File.GetAttributes(FileName).HasFlag(FileAttributes.ReadOnly) ); // as well as non checkouted source files
				}
			}
		}

		private Dictionary<string, string> ParseCommandLineOptions(List<Action> Actions, int ActionIndex, string[] SpecialOptions, out List<FInputFile> InputFiles,  bool SaveResponseFile = false)
		{
			var CompilerCommandLine = Actions[ActionIndex].CommandArguments;
			InputFiles = new List<FInputFile>();
			// (PQU) - Workaround a bug in Fastbuild path cleaning which can lead to bad escaping :
			//	Ex:
			//		/LIBPATH:"ThirdParty/DirectShow/DirectShow-1.0.0/Lib/Win64/vs2015/"
			//   => /LIBPATH:"ThirdParty\DirectShow\DirectShow-1.0.0\Lib\Win64\vs2015\"  <== OMG, this is now escaping '"'
			//
			//	I'll make a PR but while waiting for a patched version we'll be relying on this dirty, naughty workaround.
			//
			CompilerCommandLine = CompilerCommandLine.Replace("/\" ", "\" "); // remove extra '/' at the end of quoted path
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string, string>();

			// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
			CompilerCommandLine = SubstituteEnvironmentVariables(CompilerCommandLine);

			// Some tricky defines /DTROUBLE=\"\\\" abc  123\\\"\" aren't handled properly by either Unreal or Fastbuild, but we do our best.
			char[] SpaceChar = { ' ' };
			string[] RawTokens = CompilerCommandLine.Trim().Split(' ');
			List<string> ProcessedTokens = new List<string>();
			bool QuotesOpened = false;
			string PartialToken = "";
			string ResponseFilePath = "";

			if (RawTokens.Length >= 1 && RawTokens[0].StartsWith("@\"")) //Response files are in 4.13 by default. Changing VCToolChain to not do this is probably better.
			{
				string responseCommandline = RawTokens[0];

				// If we had spaces inside the response file path, we need to reconstruct the path.
				for (int i = 1; i < RawTokens.Length; ++i)
				{
					responseCommandline += " " + RawTokens[i];
				}

				ResponseFilePath = responseCommandline.Substring(2, responseCommandline.Length - 3); // bit of a bodge to get the @"response.txt" path...
				try
				{
					string ResponseFileText = File.ReadAllText(ResponseFilePath);

					// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
					ResponseFileText = SubstituteEnvironmentVariables(ResponseFileText);

					string[] Separators = { "\n", " ", "\r" };
					if (File.Exists(ResponseFilePath))
						RawTokens = ResponseFileText.Split(Separators, StringSplitOptions.RemoveEmptyEntries); //Certainly not ideal
				}
				catch (Exception e)
				{
					Console.WriteLine("Looks like a response file in: " + CompilerCommandLine + ", but we could not load it! " + e.Message);
					ResponseFilePath = "";
				}
			}

			// Raw tokens being split with spaces may have split up some two argument options and
			// paths with multiple spaces in them also need some love
			for (int i = 0; i < RawTokens.Length; ++i)
			{
				string Token = RawTokens[i];
				if (string.IsNullOrEmpty(Token))
				{
					if (ProcessedTokens.Count > 0 && QuotesOpened)
					{
						string CurrentToken = ProcessedTokens.Last();
						CurrentToken += " ";
					}

					continue;
				}

				int numQuotes = 0;
				// Look for unescaped " symbols, we want to stick those strings into one token.
				for (int j = 0; j < Token.Length; ++j)
				{
					if (Token[j] == '\\') //Ignore escaped quotes
						++j;
					else if (Token[j] == '"')
						numQuotes++;
				}

				// Defines can have escaped quotes and other strings inside them
				// so we consume tokens until we've closed any open unescaped parentheses.
				if ((Token.StartsWith("/D") || Token.StartsWith("-D")) && !QuotesOpened)
				{
					if (numQuotes == 0 || numQuotes == 2)
					{
						ProcessedTokens.Add(Token);
					}
					else
					{
						PartialToken = Token;
						++i;
						bool AddedToken = false;
						for (; i < RawTokens.Length; ++i)
						{
							string NextToken = RawTokens[i];
							if (string.IsNullOrEmpty(NextToken))
							{
								PartialToken += " ";
							}
							else if (!NextToken.EndsWith("\\\"") && NextToken.EndsWith("\"")) //Looking for a token that ends with a non-escaped "
							{
								ProcessedTokens.Add(PartialToken + " " + NextToken);
								AddedToken = true;
								break;
							}
							else
							{
								PartialToken += " " + NextToken;
							}
						}
						if (!AddedToken)
						{
							Console.WriteLine("Warning! Looks like an unterminated string in tokens. Adding PartialToken and hoping for the best. Command line: " + CompilerCommandLine);
							ProcessedTokens.Add(PartialToken);
						}
					}
					continue;
				}

				if (!QuotesOpened)
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						PartialToken = Token + " ";
						QuotesOpened = true;
					}
					else
					{
						ProcessedTokens.Add(Token);
					}
				}
				else
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						ProcessedTokens.Add(PartialToken + Token);
						QuotesOpened = false;
					}
					else
					{
						PartialToken += Token + " ";
					}
				}
			}

			//Processed tokens should now have 'whole' tokens, so now we look for any specified special options
			foreach (string specialOption in SpecialOptions)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					if (ProcessedTokens[i] == specialOption && i + 1 < ProcessedTokens.Count)
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i + 1];
						ProcessedTokens.RemoveRange(i, 2);
						break;
					}
					else if (ProcessedTokens[i].StartsWith(specialOption))
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i].Replace(specialOption, null);
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}
			// (PQU) - Input file should be present in current transaction !
			int InputTokenIndex = -1;

			// The search for the input file... we take the first non-argument we can find
			for (int i = 0; i < ProcessedTokens.Count; ++i)
			{
				string Token = ProcessedTokens[i];

				if (Token == "/I" || Token == "/l" || Token == "/D" || Token == "-D" || Token == "-x" || Token == "-include") // Skip tokens with values, I for cpp includes, l for resource compiler includes
				{
					++i;
					continue;
				}

				if (Token.Length == 0 || Token.StartsWith("/") || Token.StartsWith("-"))
				{
					continue;
				}

				// (PQU) - Input files should be present in current transaction and bound to the correct action!
#if false
				ParsedCompilerOptions["InputFile"] = Token;
				ProcessedTokens.RemoveAt(i);
				break;
#else
				Token = Token.Trim(' ').Trim('"').Trim('\'');
				int DependentActionIndex;
				if (ProducedItemAbsolutePathToActionIndex.TryGetValue(Token, out DependentActionIndex))
				{
					if (DependentActionIndex == ActionIndex)
					{
						ParsedCompilerOptions["OutputFile"] = Token;
					}
					else
					{
						InputFiles.Add(new FInputFile(DependentActionIndex, Token));
					}

					ProcessedTokens.RemoveAt(i--);
				}
				else if (InputTokenIndex == -1)
				{
					InputTokenIndex = i; // only keep first input file
				}
#endif
			}

			// (PQU) - Still fallback on last InputFile if no action was found
			if (InputTokenIndex != -1 && InputFiles.Count == 0)
			{
				InputFiles.Add(new FInputFile(ProcessedTokens[InputTokenIndex].Trim(' ').Trim('"').Trim('\'')));
				ProcessedTokens.RemoveAt(InputTokenIndex);
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", ProcessedTokens) + " ";

			if (SaveResponseFile && !string.IsNullOrEmpty(ResponseFilePath))
			{
				ParsedCompilerOptions["@"] = ResponseFilePath;
			}

			return ParsedCompilerOptions;
		}

		private List<Action> SortActions(List<Action> InActions)
		{
			List<Action> Actions = InActions;

			int NumSortErrors = 0;
			for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
			{
				Action Action = InActions[ActionIndex];
				foreach (FileItem Item in Action.PrerequisiteItems)
				{
					if (Item.ProducingAction != null && InActions.Contains(Item.ProducingAction))
					{
						int DepIndex = InActions.IndexOf(Item.ProducingAction);
						if (DepIndex > ActionIndex)
						{
							NumSortErrors++;
						}
					}
				}
			}
			if (NumSortErrors > 0)
			{
				Actions = new List<Action>();
				var UsedActions = new HashSet<int>();
				for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
				{
					if (UsedActions.Contains(ActionIndex))
					{
						continue;
					}
					Action Action = InActions[ActionIndex];
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null && InActions.Contains(Item.ProducingAction))
						{
							int DepIndex = InActions.IndexOf(Item.ProducingAction);
							if (UsedActions.Contains(DepIndex))
							{
								continue;
							}
							Actions.Add(Item.ProducingAction);
							UsedActions.Add(DepIndex);
						}
					}
					Actions.Add(Action);
					UsedActions.Add(ActionIndex);
				}
				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null && Actions.Contains(Item.ProducingAction))
						{
							int DepIndex = Actions.IndexOf(Item.ProducingAction);
							if (DepIndex > ActionIndex)
							{
								Console.WriteLine("Action is not topologically sorted.");
								Console.WriteLine("  {0} {1}", Action.CommandPath, Action.CommandArguments);
								Console.WriteLine("Dependency");
								Console.WriteLine("  {0} {1}", Item.ProducingAction.CommandPath, Item.ProducingAction.CommandArguments);
								throw new BuildException("Cyclical Dependency in action graph.");
							}
						}
					}
				}
			}

			return Actions;
		}

		private string GetOptionValue(Dictionary<string, string> OptionsDictionary, string Key, Action Action, bool ProblemIfNotFound = false)
		{
			string Value = string.Empty;
			if (OptionsDictionary.TryGetValue(Key, out Value))
			{
				// (SHO) -- do not try to trim " if beginning and end characters differ!
				if (Value.First() != Value.Last())
					return Value;
				return Value.Trim(new Char[] { '\"' });
			}

			if (ProblemIfNotFound)
			{
				Console.WriteLine("We failed to find " + Key + ", which may be a problem.");
				Console.WriteLine("Action.CommandArguments: " + Action.CommandArguments);
			}

			return Value;
		}

		public string GetRegistryValue(string keyName, string valueName, object defaultValue)
		{
			object returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			return defaultValue.ToString();
		}

		private void WriteEnvironmentSetup()
		{
			//System.Diagnostics.Debugger.Launch();

			VCEnvironment VCEnv = null;
			DirectoryReference VSInstallDir = null;
			try
			{
				// This may fail if the caller emptied PATH; we try to ignore the problem since
				// it probably means we are building for another platform.
				if (BuildType == FBBuildType.Windows || BuildType == FBBuildType.XBOne)
				{
					var TargetRules = new WindowsTargetRules();
					var TargetCompiler = WindowsPlatform.GetDefaultCompiler(null);

					VCEnv = VCEnvironment.Create(
						TargetCompiler, CppPlatform.Win64,
						TargetRules.CompilerVersion, TargetRules.WindowsSdkVersion);

					VSInstallDir = WindowsPlatform.FindVSInstallDirs(TargetCompiler).Where(x => !x.ToString().Contains("2019")).First();
				}
			}
			catch (Exception)
			{
				Console.WriteLine("Failed to get Visual Studio environment.");
			}

			// Copy environment into a case-insensitive dictionary for easier key lookups
			Dictionary<string, string> envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				envVars[(string)entry.Key] = (string)entry.Value;
			}

			if (envVars.ContainsKey("CommonProgramFiles"))
			{
				AddText("#import CommonProgramFiles\n");
			}

			if (envVars.ContainsKey("DXSDK_DIR"))
			{
				AddText("#import DXSDK_DIR\n");
			}

			if (envVars.ContainsKey("DurangoXDK"))
			{
				AddText("#import DurangoXDK\n");
			}

			if (VCEnv != null)
			{
				string platformVersionNumber = "VSVersionUnknown";

				switch (VCEnv.Compiler)
				{
					case WindowsCompiler.VisualStudio2015:
						platformVersionNumber = "140";
						break;

					case WindowsCompiler.VisualStudio2017:
						platformVersionNumber = "140"; // will only change for VS2019
						break;

					default:
						string exceptionString = "Error: Unsupported Visual Studio Version.";
						Console.WriteLine(exceptionString);
						throw new BuildException(exceptionString);
				}

				var VCToolPath64 = VCEnv.CompilerPath.Directory;

				string cluiPath = "1033/clui.dll"; // Check English first...
				if (!File.Exists(VCToolPath64 + cluiPath))
				{
					var numericDirectories = Directory.GetDirectories(VCToolPath64.ToString()).Where(d => Path.GetFileName(d).All(char.IsDigit));
					var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
					if (cluiDirectories.Any())
						cluiPath = $"{Path.GetFileName(cluiDirectories.First())}/clui.dll";
				}

				string crtPath = $"{VSInstallDir.FullName}/VC/redist";
				if (VCEnv.Compiler == WindowsCompiler.VisualStudio2015)
					crtPath += $"/x64/Microsoft.VC{platformVersionNumber}.CRT";
				else
					crtPath += "/MSVC/14.16.27012/x64/Microsoft.VC141.CRT";

				AddText($@"
				.ResourceCompilerPath = '{VCEnv.ResourceCompilerPath}'

				Compiler('UE4ResourceCompiler')
				{{
				    .Executable = '$ResourceCompilerPath$'
				    .CompilerFamily  = 'custom'
				}}

				Compiler('UE4Compiler')
				{{
				    .Root = '{VCToolPath64}'
				    .Executable = '$Root$/cl.exe'
				    .AllowDistribution = true
				    .CompilerFamily = 'msvc' ; (PQU) - Force identification of MSVC compiler for distribution/caching
				    .ExtraFiles =
				    {{
				        '$Root$/c1.dll'
				        '$Root$/c1xx.dll'
				        '$Root$/c2.dll'
				        '$Root$/{cluiPath}'
				        '$Root$/mspdbsrv.exe'
				        '$Root$/mspdbcore.dll'
				        '$Root$/mspft{platformVersionNumber}.dll'
				        '$Root$/msobj{platformVersionNumber}.dll'
				        '$Root$/mspdb{platformVersionNumber}.dll'
				        '{crtPath}/msvcp{platformVersionNumber}.dll'
				        '{crtPath}/vccorlib{platformVersionNumber}.dll'
				    }}
				}}
				");
			}

			if (envVars.ContainsKey("SCE_ORBIS_SDK_DIR"))
			{
				AddText($@"
				.SCE_ORBIS_SDK_DIR = '{envVars["SCE_ORBIS_SDK_DIR"]}'
				.PS4BasePath = '{envVars["SCE_ORBIS_SDK_DIR"]}/host_tools/bin'

				Compiler('UE4PS4Compiler')
				{{
				    .Executable = '$PS4BasePath$/orbis-clang.exe'
				    .AllowDistribution = true
				    .CompilerFamily = 'clang' ; (PQU) - Force identification of ORBIS compiler for distribution/caching
				}}
				");
			}

			AddText($@"
			Settings
			{{
			    .CachePath = '{(bEnableCaching ? CachePath : "")}' ; Optional cachePath user setting
			    .RootPath = '{UnrealBuildTool.RootDirectory}'
			    .Environment =
			    {{
			        ""PATH={(VCEnv == null ? envVars.ContainsKey("PATH") ? envVars["PATH"] : "" : $"{VSInstallDir}\\Common7\\IDE\\;{VCEnv.CompilerPath.Directory}")}"",
			        ""TMP={(envVars.ContainsKey("TMP") ? envVars["TMP"] : "")}"",
			        ""SystemRoot={(envVars.ContainsKey("SystemRoot") ? envVars["SystemRoot"] : "")}"",
			        ""INCLUDE={(envVars.ContainsKey("INCLUDE") ? envVars["INCLUDE"] : "")}"",
			        ""LIB={(envVars.ContainsKey("LIB") ? envVars["LIB"] : "")}"",
			        ""LIBPATH={(envVars.ContainsKey("LIBPATH") ? envVars["LIBPATH"] : "")}"",
			    }}
			}}
			");
		}

		private void AddCompileAction(List<Action> Actions, Action Action, int ActionIndex, List<int> DependencyIndices)
		{
			string CompilerName = GetCompilerName();
			if (Action.CommandPath.Contains("rc.exe"))
			{
				CompilerName = "UE4ResourceCompiler";
			}
			
			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o" };
			// (PQU) - we now correctly parse *all* input files
			List<FInputFile> InputFiles;
			var ParsedCompilerOptions = ParseCommandLineOptions(Actions, ActionIndex, SpecialCompilerOptions, out InputFiles);

			string OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, IsMSVC() ? "/Fo" : "-o", Action, ProblemIfNotFound: !IsMSVC());

			if (IsMSVC() && string.IsNullOrEmpty(OutputObjectFileName)) // Didn't find /Fo, try /fo
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				Console.WriteLine("We have no OutputObjectFileName. Bailing.");
				return;
			}

			string IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				Console.WriteLine("We have no IntermediatePath. Bailing.");
				Console.WriteLine("Our Action.CommandArguments were: " + Action.CommandArguments);
				return;
			}

			if (InputFiles.Count == 0)
			{
				Console.WriteLine("We have no InputFile. Bailing.");
				return;
			}

			// (PQU) - treat PCH creation as an exec command without distribution or caching
			if (ParsedCompilerOptions.ContainsKey("/Yc")) //Create PCH
			{
				string ResponseFileName = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate", "Build", $"Action_{ActionIndex}.response");
				File.WriteAllText(ResponseFileName, SubstituteEnvironmentVariables(Action.CommandArguments));

				var DependencyNames = DependencyIndices.ConvertAll(x => $"'Action_{x}'").ToArray();

				// Use Exec() instead of ObjectList() to make this node point directly to OutputObjectFileName
				AddText($@"
				Exec('Action_{ActionIndex}')
				{{
				    .AllowDistribution = false ; Can't use distribution for PCH
				    .ExecExecutable = '{Action.CommandPath}'
				    .ExecArguments = '@""{ResponseFileName}""'
				    .ExecOutput = '{OutputObjectFileName}'
				    .ExecInput = {{ {string.Join(",", InputFiles.Select(x => $"'{x}'"))} }}
				    .PreBuildDependencies = {{ {string.Join(",", DependencyNames)} }}
				}}
				");
				return;
			}

			string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);
			string CompilerOptions = "";
			string CompilerOutputExtension = ".unset";
			string AdditionalFlags = "";

#if false // (PQU) - treat PCH creation as an exec command without distribution or caching
			if (ParsedCompilerOptions.ContainsKey("/Yc")) //Create PCH
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yc", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);

				AdditionalFlags = $"\t.PCHOptions = '\"%1\" /Fp\"%2\" /Yc\"{PCHIncludeHeader}\" {OtherCompilerOptions} /Fo\"{OutputObjectFileName}\"'\n"
				                + $"\t.PCHInputFile = \"{InputFile}\"\n"
				                + $"\t.PCHOutputFile = \"{PCHOutputFile}\"\n";

				CompilerOptions = $"\"%1\" /Fo\"%2\" /Fp\"{PCHOutputFile}\" /Yu\"{PCHIncludeHeader}\" {OtherCompilerOptions}";
				CompilerOutputExtension = ".obj";
			}
			else
#endif
			if (ParsedCompilerOptions.ContainsKey("/Yu")) //Use PCH
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yu", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);
				string PCHToForceInclude = PCHOutputFile.Replace(".pch", "");

				CompilerOptions = $"\"%1\" /Fo\"%2\" /Fp\"{PCHOutputFile}\" /Yu\"{PCHIncludeHeader}\" /FI\"{PCHToForceInclude}\" {OtherCompilerOptions}";
				CompilerOutputExtension = ".cpp.obj";
			}
			else if (CompilerName == "UE4ResourceCompiler")
			{
				if (InputFiles.Count != 1)
					throw new BuildException("Should have only one InputFile for resource compiler !");
				CompilerOptions = $"{OtherCompilerOptions} /fo\"%2\" \"%1\"";
				CompilerOutputExtension = Path.GetExtension(InputFiles[0].FileName) + ".res";
			}
			else if (IsMSVC())
			{
				CompilerOptions = $"{OtherCompilerOptions} /Fo\"%2\" \"%1\"";
				CompilerOutputExtension = ".cpp.obj";
			}
			else
			{
				CompilerOptions = $"{OtherCompilerOptions} -o \"%2\" \"%1\"";
				CompilerOutputExtension = ".cpp.o";
			}

			var preBuildDependencies = DependencyIndices.ConvertAll(x => $"'Action_{x}'");

			bool bAllowCaching = true;
			bool bAllowDistribution = Action.bCanExecuteRemotely;

			// (PQU) Don't use caching at all on dev machines for files excluded from unity builds (avoid preprocessing)
			if (DNEIsBuildBotMachine() == false)
			{
				// This is the same check which is used by Epic inside UEBuildModuleCPP.CompileUnityFilesWithToolChain
				bAllowCaching = !InputFiles.Any(x => !x.AllowCaching);

				if (bAllowCaching == false)
				{
					Console.WriteLine($"[FASTBUILD] Disabled caching for : {string.Join(",", InputFiles.Select(x => $"'{Path.GetFileName(x.FileName)}'"))}");
				}
			}

			// (SHO) - workaround for ICE
			// https://developercommunity.visualstudio.com/content/problem/314797/internal-compiler-error-18.html
			// https://developercommunity.visualstudio.com/content/problem/313306/vs2017-158-internal-compiler-error-msc1cpp-line-15-1.html
			bool bIsProblematic = IntermediatePath.Contains("VisualStudioSourceCodeAccess") || IntermediatePath.Contains("DatabaseSupport");

			if (bIsProblematic)
			{
				bAllowCaching = false;
				bAllowDistribution = false;
			}

			AddText($@"
			ObjectList('Action_{ActionIndex}')
			{{
			    .Compiler = '{CompilerName}'
			    .CompilerInputFiles = {{ {string.Join(",", InputFiles.Select(x => $"'{x}'"))} }}
			    .CompilerOutputPath = ""{IntermediatePath}""
			    .CompilerOptions = '{CompilerOptions}'
			    .CompilerOutputExtension = '{CompilerOutputExtension}'
				.AllowCaching = {(bAllowCaching ? "true" : "false")}
				.AllowDistribution = {(bAllowDistribution ? "true" : "false")}
			    {AdditionalFlags}
			    .PreBuildDependencies = {{ {string.Join(",", preBuildDependencies)} }}
			}}
			");
		}

		private void AddLinkAction(List<Action> Actions, int ActionIndex, List<int> DependencyIndices)
		{
			Action Action = Actions[ActionIndex];
			// (SHO) - fix PS4 build
			// -output must appear before -o
			string[] SpecialLinkerOptions = { "/OUT:", "@", "-output=", "-o", "-self=" };
			List<FInputFile> InputFiles;
			var ParsedLinkerOptions = ParseCommandLineOptions(Actions, ActionIndex, SpecialLinkerOptions, out InputFiles, SaveResponseFile: true);

			string OutputFile;

			if (IsSymbolTool(Action))
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "-output=", Action, ProblemIfNotFound: false);
				if(string.IsNullOrEmpty(OutputFile))
				{
					OutputFile = ParsedLinkerOptions["OtherOptions"].Trim(' ').Trim('"');
				}
			}
			else if (IsMSVC())
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "/OUT:", Action, ProblemIfNotFound: true);
			}
			else //PS4
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "-o", Action, ProblemIfNotFound: false);
				if (string.IsNullOrEmpty(OutputFile))
				{
					OutputFile = GetOptionValue(ParsedLinkerOptions, "OutputFile", Action, ProblemIfNotFound: true);
					if (string.IsNullOrEmpty(OutputFile))
						throw new BuildException("Failed to find output file. Aborting.");
				}
			}

			if (string.IsNullOrEmpty(OutputFile))
			{
				Console.WriteLine("Failed to find output file. Bailing.");
				return;
			}

			if (IsSymbolTool(Action))
			{
				string InputFile = GetOptionValue(ParsedLinkerOptions, "-self=", Action, ProblemIfNotFound: false);
				string ExecInput = "";
				string ExecOutput = OutputFile;

				if (string.IsNullOrEmpty(InputFile))
					InputFile = InputFiles[0].FileName; // XBox One
				else
					ExecOutput += "symbols.bin"; // PS4

				if (string.IsNullOrEmpty(InputFile))
					ExecInput = string.Join(",", InputFiles.Select(x => $"'{x}'"));
				else
					ExecInput = $"'{InputFile}'";

				AddText($@"
				Exec('Action_{ActionIndex}')
				{{
				    .ExecExecutable = '{Action.CommandPath}'
				    .ExecArguments = '{SubstituteEnvironmentVariables(Action.CommandArguments)}'
				    .ExecOutput = '{ExecOutput}'
				    .ExecInput = {{ {ExecInput} }}
				}}
				");
				return;
			}

			string ResponseFilePath = GetOptionValue(ParsedLinkerOptions, "@", Action);
			string OtherCompilerOptions = GetOptionValue(ParsedLinkerOptions, "OtherOptions", Action);

			if (Action.CommandPath.Contains("lib.exe") || Action.CommandPath.Contains("orbis-snarl"))
			{
				var preBuildDependencies = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', ; {1}",
					x, string.Join(",", Actions[x].ProducedItems.Select(y => y.AbsolutePath))));

				// (PQU) - Work around a bug with orbis-snarl, need to use a response file to dodge quoting issues
				if (IsMSVC() == false) // only PS4
				{
					string execArguments = SubstituteEnvironmentVariables(Action.CommandArguments);

					if (OtherCompilerOptions.Length > 0 || InputFiles.Count > 0)
					{
						var ResponseFileContent = new List<string>();
						ResponseFileContent.AddRange(InputFiles.Select(x => x.FileName));
						ResponseFileContent.AddRange(OtherCompilerOptions.Trim(' ').Split(' '));

						string ResponseFileName = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate", "Build", $"Action_{ActionIndex}.response");
						File.WriteAllText(ResponseFileName, SubstituteEnvironmentVariables(string.Join(Environment.NewLine, ResponseFileContent)));

						execArguments = $"\"{OutputFile}\" @\"{ResponseFileName}\"";
					}

					AddText($@"
					Exec('Action_{ActionIndex}')
					{{
					    .ExecExecutable = '{Action.CommandPath}'
					    .ExecOutput = '{OutputFile}'
					    .ExecArguments = '{execArguments}'
					    .PreBuildDependencies =
					    {{
					        {string.Join("\n", preBuildDependencies)}
					    }}
					}}
					");
					return;
				}

				string compilerOptions = IsMSVC() ? "\"%1\" /Fo\"%2\" /c" : "\"%1\" -o \"%2\" -c";
				string librarianOptions = "";
				string librarianAdditionalInputs = "";

				// (MHO) - fix Win64 warning LNK4042: object specified more than once; extras ignored
				if (string.IsNullOrEmpty(ResponseFilePath))
					librarianOptions = IsMSVC() ? $"/OUT:\"%2\" {OtherCompilerOptions} \"%1\"" : $"\"%2\" \"%1\" {OtherCompilerOptions}";
				else
					librarianOptions = IsMSVC() ? $"/OUT:\"%2\" @\"{ResponseFilePath}\"" : $"\"%2\" @\"%1\" {OtherCompilerOptions}";

				if (string.IsNullOrEmpty(ResponseFilePath))
				{
					if (InputFiles.Count == 0)
						throw new BuildException("Failed to find input file(s). Bailing.");
					librarianAdditionalInputs = string.Join(",", InputFiles.Select(x => $"'{x}'"));
				}
				else if (IsMSVC())
					librarianAdditionalInputs = $"'Action_{DependencyIndices[0]}'"; // Hack...Because FastBuild needs at least one Input file
				else
					librarianAdditionalInputs = $"'{ResponseFilePath}'";

				AddText($@"
				Library('Action_{ActionIndex}')
				{{
				    .Compiler = '{GetCompilerName()}'
				    .CompilerOptions = '{compilerOptions}'
				    .CompilerOutputPath = ""{Path.GetDirectoryName(OutputFile)}""
				    .Librarian = '{Action.CommandPath}'
				    .LibrarianOptions = '{librarianOptions}'
				    .LibrarianAdditionalInputs = {{ {librarianAdditionalInputs} }}
				    .PreBuildDependencies =
				    {{
				        {string.Join("\n", preBuildDependencies)}
				    }}
				    .LibrarianOutput = '{OutputFile}'
				}}
				");
				return;
			}

			if (Action.CommandPath.Contains("link.exe") || Action.CommandPath.Contains("orbis-clang"))
			{
				string ActionType = "Executable";
				string AdditionalFlags = "";
				string LinkerOptions = "";

				// (PQU) - Fix DLL compilation for editor
				string DynamicLinkLibraryExt = UEBuildPlatform.GetBuildPlatform(IsMSVC() ? UnrealTargetPlatform.Win64 : UnrealTargetPlatform.PS4).GetBinaryExtension(UEBuildBinaryType.DynamicLinkLibrary);
				if (OutputFile.EndsWith(DynamicLinkLibraryExt))
				{
					ActionType = "DLL";
					AdditionalFlags += ".LinkerLinkObjects = false\n"; // Default is true, http://www.fastbuild.org/docs/functions/dll.html
				}

				// (PQU) - every step, including link, should have at least one input
				if (InputFiles.Count == 0)
				{
					Console.WriteLine("Failed to find input file(s). Bailing.");
					return;
				}

				// 1) UBT for XB1 does not put compiler options in the response file, so can't use IsMSVC()
				// 2) DetectBuildType() is global, so can't really rely on BuildType == FBBuildType.XBOne
				// 3) XB1 uses standard VC linker so FBBuildType.XBOne is never detected anyway
				// (PQU) - No more response files generated by UBT when using FastBuild
				if (BuildType == FBBuildType.XBOne)
					LinkerOptions = $"/Out:\"%2\" {OtherCompilerOptions} \"%1\"";
				else if (IsMSVC())
					LinkerOptions = $"/Out:\"%2\" {OtherCompilerOptions} \"%1\"";
				else
				{
					AdditionalFlags += ".LinkerType = 'clang-orbis'\n"; // force Fastbuild to identify as orbis linker
					LinkerOptions = $"-o \"%2\" {OtherCompilerOptions} \"%1\"";
				}
#if false
				// The TLBOUT and MQ are huge bodges to consume the %1
				if (BuildType == FBBuildType.XBOne)
					LinkerOptions = $"/TLBOUT:\"%1\" /Out:\"%2\" @\"{ResponseFilePath}\" {OtherCompilerOptions}";
				else if (IsMSVC())
					LinkerOptions = $"/TLBOUT:\"%1\" /Out:\"%2\" @\"{ResponseFilePath}\"";
				else
					LinkerOptions = $"-o \"%2\" @\"{ResponseFilePath}\" {OtherCompilerOptions} -MQ \"%1\"";
#endif

				var preBuildDependencies = DependencyIndices.ConvertAll(x => $"'Action_{x}'");

				AddText($@"
				{ActionType}('Action_{ActionIndex}')
				{{
				    .Linker = '{Action.CommandPath}'
				    .Libraries = {{ {string.Join(",", InputFiles.Select(x => $"'{x}'"))} }}
				    .LinkerOptions = '{LinkerOptions}'
				    .LinkerOutput = '{OutputFile}'
				    {AdditionalFlags}
				    .PreBuildDependencies = {{ {string.Join(",", preBuildDependencies)} }}
				}}
				");
				return;
			}
		}

		private FileStream bffOutputFileStream = null;
		private Dictionary<string, int> ProducedItemAbsolutePathToActionIndex = new Dictionary<string, int>();

		private bool CreateBffFile(List<Action> InActions, string BffFilePath)
		{
			// (PQU) - Put some logs for debugging BFF generation
			Console.WriteLine($"Creating BFF {BffFilePath} ...");
			List<Action> Actions = SortActions(InActions);

			ProducedItemAbsolutePathToActionIndex.Clear();
			for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
			{
				foreach (FileItem ProducedItem in Actions[ActionIndex].ProducedItems)
				{
					ProducedItemAbsolutePathToActionIndex.Add(ProducedItem.AbsolutePath, ActionIndex);
				}
			}

			try
			{
				bffOutputFileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write);

				WriteEnvironmentSetup(); //Compiler, environment variables and base paths

				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];

					// Resolve dependencies
					List<int> DependencyIndices = new List<int>();
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null)
						{
							int ProducingActionIndex = Actions.IndexOf(Item.ProducingAction);
							if (ProducingActionIndex >= 0)
							{
								DependencyIndices.Add(ProducingActionIndex);
							}
						}
					}

					switch (Action.ActionType)
					{
						case ActionType.Compile: AddCompileAction(Actions, Action, ActionIndex, DependencyIndices); break;
						case ActionType.Link: AddLinkAction(Actions, ActionIndex, DependencyIndices); break;
						default: Console.WriteLine("Fastbuild is ignoring an unsupported action: " + Action.ActionType.ToString()); break;
					}
				}

				var actionNames = Enumerable.Range(0, Actions.Count).ToList().ConvertAll(x => $"'Action_{x}'");

				AddText($@"
				Alias('all')
				{{
				    .Targets = {{ {string.Join(",", actionNames)} }}
				}}
				");

				bffOutputFileStream.Close();
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception while creating bff file: " + e.ToString());
				return false;
			}

			return true;
		}

		// (PQU) - helpers for buildbot machines
		private static bool DNEIsBuildBotMachine()
		{
			return (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("dne_ISBUILDMACHINE")));
		}

		// (BHA) - Override temporary directory
		// Copied from Patoune FastBuildModel
		private string GetTemporaryDirectory()
		{
			string targetPartition = null;

			if (Environment.MachineName.ToLowerInvariant().StartsWith("farm"))
			{
				// Hackish way to find a non system partition located on a fast drive
				string mainWorkerName = Environment.MachineName.ToLowerInvariant() + "a";
				targetPartition = new List<string>() { "D", "E", "F" }.Select(partition => partition + ":\\")
					.FirstOrDefault(partition => File.Exists(Path.Combine(partition, mainWorkerName, "buildbot.tac")));
			}

			if (targetPartition == null)
				targetPartition = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));

			return Path.Combine(targetPartition, "Temporary", "FastBuild");
		}

		private bool ExecuteBffFile(string BffFilePath)
		{
			string cacheArgument = "";

			if (bEnableCaching)
			{
				switch (CacheMode)
				{
					case eCacheMode.ReadOnly:
						cacheArgument = "-cacheread";
						break;
					case eCacheMode.WriteOnly:
						cacheArgument = "-cachewrite";
						break;
					case eCacheMode.ReadWrite:
						cacheArgument = "-cache";
						break;
				}
				// (PQU) - There's not much reward for dev machines uploading their locally modified TUs
				if (DNEIsBuildBotMachine() == false)
				{
					Console.WriteLine("[FASTBUILD] force cache mode to read only (dev machine detected)");
					cacheArgument = "-cacheread";
				}
			}

			string distArgument = bEnableDistribution ? "-dist" : "";

			//Interesting flags for FASTBuild: -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
			// Yassine: The -clean is to bypass the FastBuild internal dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
			//			Basically we want FB to stupidly compile what UBT tells it to.
			//use -showcmds for debugging full command lines passed to external tools as they are invoked
			//-ide enables options: -noprogress -fixuperrorpaths -wrapper
			string FBCommandLine = $" -noprogress -monitor -summary {distArgument} {cacheArgument} -clean -config {BffFilePath}";
			// (PQU) - buildbot machines get extra debug infos for offline debugging
			if (DNEIsBuildBotMachine())
			{
				Console.WriteLine("[FASTBUILD] add extra debug flags (buildbot machine detected)");
				FBCommandLine += " -report -showcmds -verbose -cacheverbose -distverbose -noprogress -nostoponerror ";
			}
			else
			{
				// (NSE) - limit the number of tasks that can be done in parallel to avoid hogging the computer resources
				//		especially on threadripper configurations (see http://www.fastbuild.org/docs/options.html#jx)
				if (Environment.ProcessorCount >= 32)
				{
					FBCommandLine += $" -j{Environment.ProcessorCount / 2}";
					Console.WriteLine("[FASTBUILD] limiting the number of parallel tasks to {0}", Environment.ProcessorCount / 2);
				}

				// (PQU) - devs want to interrupt their build without corrupting it
				FBCommandLine += " -fastcancel -ide -nosummaryonerror";
			}

			// Check global environment for an already defined value of FASTBUILD_BROKERAGE_PATH (let the user override the static value defined in this source file)
			string actualBrokerPath = Environment.GetEnvironmentVariable("FASTBUILD_BROKERAGE_PATH");
			if (actualBrokerPath == null)
			{
				actualBrokerPath = BrokerPath;
			}

			ProcessStartInfo FBStartInfo = new ProcessStartInfo(string.IsNullOrEmpty(FBuildExePathOverride) ? "fbuild" : FBuildExePathOverride, FBCommandLine);
			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory = Path.Combine(UnrealBuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()), "Source");
			FBStartInfo.EnvironmentVariables["FASTBUILD_BROKERAGE_PATH"] = actualBrokerPath; // automatically register FASTBUILD_BROKERAGE_PATH environment variable

			// (BHA) - Override temporary directory
			if (Environment.OSVersion.Platform == PlatformID.Win32NT)
			{
				// Copied from Patoune FastBuildModel
				string temporaryDirectory = GetTemporaryDirectory();
				Directory.CreateDirectory(temporaryDirectory);
				FBStartInfo.EnvironmentVariables["TMP"] = temporaryDirectory;
				FBStartInfo.EnvironmentVariables["TEMP"] = temporaryDirectory;
			}

			try
			{
				Process FBProcess = new Process();
				FBProcess.StartInfo = FBStartInfo;

				FBStartInfo.RedirectStandardError = true;
				FBStartInfo.RedirectStandardOutput = true;
				FBProcess.EnableRaisingEvents = true;

				DataReceivedEventHandler OutputEventHandler = (Sender, Args) =>
				{
					if (Args.Data != null)
						Console.WriteLine(Args.Data);
				};

				FBProcess.OutputDataReceived += OutputEventHandler;
				FBProcess.ErrorDataReceived += OutputEventHandler;

				FBProcess.Start();

				FBProcess.BeginOutputReadLine();
				FBProcess.BeginErrorReadLine();

				FBProcess.WaitForExit();
				string ExitCodeDescription = "unknown return code";
				switch( FBProcess.ExitCode )
				{
					case  0: ExitCodeDescription = "success"; break;
					case -1: ExitCodeDescription = "build failed"; break;
					case -2: ExitCodeDescription = "error loading .bff"; break;
					case -3: ExitCodeDescription = "bad arguments"; break;
					case -4: ExitCodeDescription = "already running"; break;
					case -5: ExitCodeDescription = "failed to spawn wrapper"; break;
					case -6: ExitCodeDescription = "failed to spawn final wrapper"; break;
					case -7: ExitCodeDescription = "wrapper crashed"; break;
				}
				Console.WriteLine("FBuild.exe exited with code 0x{0:X} ({1})", FBProcess.ExitCode, ExitCodeDescription);
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return false;
			}
		}
	}
}

