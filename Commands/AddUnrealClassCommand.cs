using System;
using System.ComponentModel.Design;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE;
using EnvDTE80;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.VCProjectEngine;
using System.Linq;
using System.Diagnostics;
using Newtonsoft.Json;

namespace UnrealContextMenu
{
    internal sealed class AddUnrealClassCommand
    {
        public static readonly Guid CommandSet = PackageGuids.UnrealContextMenu;
        public const int CommandId = 0x0100;

        private readonly AsyncPackage package;

        private string projectRoot;
        private string uprojectPath;
        private string sourcePath;
        private string engineRoot;
        private List<string> FilterHierarchy;

        private AddUnrealClassCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            if (!(await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService))
                return;

            new AddUnrealClassCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = (DTE2)ServiceProvider.GetService(typeof(DTE));
            if (dte == null) return;

            var solution = dte.Solution;

            EnvDTE.Project project = GetFirstRealProject(solution);
            if (project != null && !string.IsNullOrEmpty(project.FullName))
            {
                InitializePaths(project);
                InitializeEngineRoot();
            }
            else
            {
                ShowMessage("No project found to determine paths.", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            var selectedPath = GetSelectedPathFromSolutionExplorer();
            if (string.IsNullOrEmpty(selectedPath))
            {
                ShowMessage("No valid folder selected.", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            // Adjust these paths as needed or make them configurable
            var classHierarchy = BuildClassHierarchy("C:\\UnrealEngine-5.3.2\\Engine\\Source", "G:\\ALS_Refactored_UE5\\Project_ALSR\\Source");

            var dialogWindow = new BaseClassDialogWindow(classHierarchy);
            dialogWindow.ShowDialog();
            var result = dialogWindow.DialogResultValue;

            if (result == true)
            {
                string baseClass = dialogWindow.BaseClassDialog.SelectedBaseClass;
                string newClassName = dialogWindow.BaseClassDialog.NewClassName;
                GenerateUnrealClassFiles(selectedPath, baseClass, newClassName, classHierarchy);
            }
        }

        private EnvDTE.Project GetFirstRealProject(EnvDTE.Solution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (EnvDTE.Project proj in solution.Projects)
            {
                var realProject = FindRealProjectRecursive(proj);
                if (realProject != null)
                    return realProject;
            }
            return null;
        }

        private EnvDTE.Project FindRealProjectRecursive(EnvDTE.Project proj)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // If this is a real project (has a project file)
            if (!string.IsNullOrEmpty(proj.FullName) && File.Exists(proj.FullName))
            {
                return proj;
            }

            // If it's a solution folder, recurse its ProjectItems
            if (proj.ProjectItems != null)
            {
                foreach (EnvDTE.ProjectItem item in proj.ProjectItems)
                {
                    // A ProjectItem might contain a subproject
                    if (item.SubProject != null)
                    {
                        var result = FindRealProjectRecursive(item.SubProject);
                        if (result != null)
                            return result;
                    }
                }
            }

            return null;
        }

        private void InitializePaths(EnvDTE.Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string startDir = Path.GetDirectoryName(project.FullName);
            projectRoot = FindProjectRoot(startDir);

            if (string.IsNullOrEmpty(projectRoot))
            {
                ShowMessage("Unable to locate .uproject file. Please ensure this is a valid Unreal project.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            var uprojectFiles = Directory.GetFiles(projectRoot, "*.uproject", SearchOption.TopDirectoryOnly);
            if (uprojectFiles.Length == 0)
            {
                ShowMessage("No .uproject file found in project root.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }
            uprojectPath = uprojectFiles[0];

            sourcePath = Path.Combine(projectRoot, "Source");
            if (!Directory.Exists(sourcePath))
            {
                ShowMessage("No Source directory found. You can still create one or choose another place.", OLEMSGICON.OLEMSGICON_WARNING);
            }
        }

        private void InitializeEngineRoot()
        {
            string engineAssoc = GetEngineAssociation();
            if (!string.IsNullOrEmpty(engineAssoc))
            {
                engineRoot = FindEngineRootFromAssociation(engineAssoc);
                if (!string.IsNullOrEmpty(engineRoot))
                    return;
            }

            // Fallback: try to find Engine folder by going up
            var dir = new DirectoryInfo(projectRoot);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Engine")))
                {
                    engineRoot = dir.FullName;
                    return;
                }
                dir = dir.Parent;
            }

            ShowMessage("Unable to determine Unreal Engine location. Please specify Engine root.", OLEMSGICON.OLEMSGICON_INFO);
        }

        private string GetSelectedPathFromSolutionExplorer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = (DTE2)ServiceProvider.GetService(typeof(DTE));
            if (dte != null && dte.SelectedItems.Count == 1)
            {
                var selItem = dte.SelectedItems.Item(1);

                var project = selItem.ProjectItem?.ContainingProject ?? selItem.Project;
                if (project == null || string.IsNullOrEmpty(project.FullName))
                    return null;

                string projectDir = Path.GetDirectoryName(project.FullName);
                string root = FindProjectRoot(projectDir);
                if (string.IsNullOrEmpty(root))
                    return null;

                if (selItem.ProjectItem != null)
                {
                    FilterHierarchy = GetFilterHierarchy(selItem.ProjectItem);
                    return BuildPhysicalPathFromHierarchy(root, FilterHierarchy);
                }

                // If user selected the project node, return root/Source
                return Path.Combine(root, "Source");
            }

            return null;
        }

        private Dictionary<string, ClassInfo> BuildClassHierarchy(string engineSourceDir, string projectSourceDir)
        {
            var db = new Dictionary<string, ClassInfo>(StringComparer.OrdinalIgnoreCase);
            ScanDirectoryForUClasses(engineSourceDir, db);
            ScanDirectoryForUClasses(projectSourceDir, db);
            return db;
        }

        private void ScanDirectoryForUClasses(string directory, Dictionary<string, ClassInfo> db)
        {
            foreach (var header in Directory.GetFiles(directory, "*.h", SearchOption.AllDirectories))
            {
                ParseForUClass(header, db);
            }
        }

        private static void ParseForUClass(string headerPath, Dictionary<string, ClassInfo> db)
        {
            var text = File.ReadAllText(headerPath);
            var pattern = @"UCLASS\s*\(.*?\)\s*class\s+(\w+_API\s+)?(?<className>\w+)\s*:\s*public\s+(?<baseClass>\w+)";
            var match = Regex.Match(text, pattern, RegexOptions.Singleline);
            if (match.Success)
            {
                var className = match.Groups["className"].Value;
                var baseClass = match.Groups["baseClass"].Value;
                if (!db.ContainsKey(className))
                {
                    db[className] = new ClassInfo
                    {
                        ClassName = className,
                        BaseClassName = baseClass,
                        FilePath = headerPath
                    };
                }
            }
        }

        private string FindProjectRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                var uproject = dir.GetFiles("*.uproject", SearchOption.TopDirectoryOnly);
                if (uproject.Length > 0)
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private List<string> GetFilterHierarchy(ProjectItem projectItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var hierarchy = new List<string>();
            var current = projectItem;
            while (current != null)
            {
                hierarchy.Insert(0, current.Name);
                if (current.Collection?.Parent is ProjectItem parentItem)
                    current = parentItem;
                else
                    break;
            }
            return hierarchy;
        }

        private string BuildPhysicalPathFromHierarchy(string projectRoot, List<string> hierarchy)
        {
            if (hierarchy.Count == 0 || !hierarchy[0].Equals("Source", StringComparison.OrdinalIgnoreCase))
            {
                hierarchy.Insert(0, "Source");
            }
            return Path.Combine(projectRoot, Path.Combine(hierarchy.ToArray()));
        }

        private const string HeaderTemplate =
@"#pragma once

#include ""CoreMinimal.h""
#include ""{BASECLASS_HEADER}""

UCLASS()
class {MODULE_API} {CLASS_NAME} : public {BASE_CLASS}
{
    GENERATED_BODY()

public:
    {CLASS_NAME}();
};";

        private const string CppTemplate =
@"#include ""{CLASS_NAME}.h""

{CLASS_NAME}::{CLASS_NAME}()
{
    // Constructor
}";

        private void GenerateUnrealClassFiles(string selectedPath, string baseClass, string newClassName, Dictionary<string, ClassInfo> classHierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var hierarchy = FilterHierarchy;
            bool isPublic = hierarchy.Exists(h => h.Equals("Public", StringComparison.OrdinalIgnoreCase));
            bool isPrivate = hierarchy.Exists(h => h.Equals("Private", StringComparison.OrdinalIgnoreCase));

            Directory.CreateDirectory(selectedPath);

            string hPath, cppPath;
            if (isPublic)
            {
                hPath = Path.Combine(selectedPath, newClassName + ".h");
                string privatePath = selectedPath.Replace("Public", "Private");
                if (!Directory.Exists(privatePath))
                    Directory.CreateDirectory(privatePath);
                cppPath = Path.Combine(privatePath, newClassName + ".cpp");
            }
            else if (isPrivate)
            {
                hPath = Path.Combine(selectedPath, newClassName + ".h");
                cppPath = Path.Combine(selectedPath, newClassName + ".cpp");
            }
            else
            {
                hPath = Path.Combine(selectedPath, newClassName + ".h");
                cppPath = Path.Combine(selectedPath, newClassName + ".cpp");
            }

            var baseInfo = classHierarchy[baseClass];
            string moduleApi = "MYPROJECT_API"; // Update as needed
            string baseHeader = Path.GetFileName(baseInfo.FilePath);

            string hContent = HeaderTemplate
                .Replace("{BASECLASS_HEADER}", baseHeader)
                .Replace("{MODULE_API}", moduleApi)
                .Replace("{CLASS_NAME}", newClassName)
                .Replace("{BASE_CLASS}", baseClass);

            string cppContent = CppTemplate.Replace("{CLASS_NAME}", newClassName);

//             File.WriteAllText(hPath, hContent);
//             File.WriteAllText(cppPath, cppContent);
// 
//             RegenerateProjectFiles(projectRoot);
        }

        private void RegenerateProjectFiles(string projectRoot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var uprojectFile = Directory.GetFiles(projectRoot, "*.uproject", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (uprojectFile == null)
            {
                ShowMessage("UProject not found.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            string batchPath = Path.Combine(engineRoot ?? projectRoot, "Engine", "Build", "BatchFiles", "GenerateProjectFiles.bat");
            if (!File.Exists(batchPath))
            {
                ShowMessage("GenerateProjectFiles.bat not found. Please configure engine path.", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = batchPath,
                Arguments = $" -project=\"{uprojectFile}\" -game -engine",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = engineRoot ?? projectRoot
            };

            var process = new System.Diagnostics.Process { StartInfo = startInfo };
            process.Start();
            process.WaitForExit();

            ReloadSolution();
        }

        private void ReloadSolution()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            
            var dte = (DTE2)ServiceProvider.GetService(typeof(DTE));
            if (dte == null) return;

            string solutionPath = dte.Solution.FullName;
            dte.Solution.Close(true);
            dte.Solution.Open(solutionPath);
        }

        private string GetEngineAssociation()
        {
            if (string.IsNullOrEmpty(uprojectPath))
                return null;

            string jsonText = File.ReadAllText(uprojectPath);
            var jsonObj = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonText);

            if (jsonObj.TryGetValue("EngineAssociation", out var engineAssoc) && engineAssoc is string assocStr)
            {
                return assocStr;
            }

            return null;
        }

        private string FindEngineRootFromAssociation(string engineAssoc)
        {
            // Check user registry
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Epic Games\\Unreal Engine\\Builds"))
            {
                if (key != null)
                {
                    string val = key.GetValue(engineAssoc) as string;
                    if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                        return val;
                }
            }

            // Check machine registry
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("Software\\EpicGames\\Unreal Engine\\Builds"))
            {
                if (key != null)
                {
                    string val = key.GetValue(engineAssoc) as string;
                    if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                        return val;
                }
            }

            return null;
        }

        private System.IServiceProvider ServiceProvider => package;

        private void ShowMessage(string message, OLEMSGICON icon)
        {
            VsShellUtilities.ShowMessageBox(
                package,
                message,
                "Unreal Context Menu",
                icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
            );
        }
    }

    public class ClassInfo
    {
        public string ClassName { get; set; }
        public string BaseClassName { get; set; }
        public string FilePath { get; set; }
    }
}
