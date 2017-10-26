﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using CodeElements.UpdateSystem.Core;
using CodeElements.UpdateSystem.Windows.Patcher;
using CodeElements.UpdateSystem.Windows.Patcher.Translations;
using Newtonsoft.Json;

namespace CodeElements.UpdateSystem.Windows
{
    /// <summary>
    ///     Defines the default patcher for Microsoft Windows
    /// </summary>
    public class WindowsPatcher : WindowsPatcherConfig, IEnvironmentManager
    {
        private readonly IApplicationCloser _applicationCloser;

        /// <summary>
        ///     Initialize a new instance of <see cref="WindowsPatcher" /> using an application closer
        /// </summary>
        /// <param name="applicationCloser">The application closer defines the procedure to safely shutdown this application</param>
        public WindowsPatcher(IApplicationCloser applicationCloser)
        {
            _applicationCloser = applicationCloser;
            Arguments = new List<UpdateArgument>();
            ApplicationPath = Assembly.GetEntryAssembly().Location;
            BaseDirectory = Path.GetDirectoryName(ApplicationPath);
            Language = WindowsUpdaterTranslation.GetByCulture(CultureInfo.CurrentUICulture);
        }

        /// <summary>
        ///     Make the host application not close. Do not use this application closer unless you are sure that no application
        ///     depdent files get patched
        /// </summary>
        public static IApplicationCloser None { get; } = null;

        /// <summary>
        ///     Defines whether the patcher should be started with administrator privileges. If this property is set to
        ///     <code>false</code>, it will inherit the privileges of the host application (default Windows behavior).
        /// </summary>
        public bool RunAsAdministrator { get; set; } = true;

        /// <summary>
        ///     Use a custom updater user interface.
        /// </summary>
        public UpdaterUi CustomUi { get; set; }

        /// <summary>
        /// The language of the updater 
        /// </summary>
        public IWindowsUpdaterTranslation Language { get; set; }

        public void Cleanup(Guid projectGuid)
        {
            foreach (var directory in Directory.GetDirectories(Path.GetTempPath(),
                $"CodeElements.UpdateSystem.{projectGuid:D}*", SearchOption.TopDirectoryOnly))
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (IOException)
                {
                    continue;
                }

            foreach (var directory in Directory.GetDirectories(BaseDirectory, "CodeElements.UpdateSystem.Backup*"))
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (IOException)
                {
                    continue;
                }
        }

        DirectoryInfo IEnvironmentManager.GetEmptyTempDirectory(Guid projectGuid)
        {
            var directory =
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"CodeElements.UpdateSystem.{projectGuid:D}"));
            if (directory.Exists)
                try
                {
                    directory.Delete(true);
                }
                catch (IOException)
                {
                    //if the deletion failed, use a unique directory name
                    directory = new DirectoryInfo(FileExtensions.MakeDirectoryUnique(directory.FullName));
                }

            return directory;
        }

        void IEnvironmentManager.DeleteTempDirectory(DirectoryInfo directoryInfo)
        {
            directoryInfo.Delete(true);
        }

        internal static string TranslateFilename(string filename, string baseDirectory)
        {
            if (filename.StartsWith("%basedir%", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(baseDirectory, filename.Substring("%basedir%".Length));
            return Environment.ExpandEnvironmentVariables(filename);
        }

        FileInfo IEnvironmentManager.TranslateFilename(string filename)
        {
            return new FileInfo(TranslateFilename(filename, BaseDirectory));
        }

        void IEnvironmentManager.ExecuteUpdater(PatcherConfig patcherConfig)
        {
            //the patcher directory will contain the patcher executable file aswell as it's dependencies
            var patcherDirectory = new DirectoryInfo(Path.Combine(patcherConfig.TempDirectory, "patcher"));
            patcherDirectory.Create();

            //copy patcher assembly
            var patcherAssembly = new FileInfo(Assembly.GetAssembly(typeof(WindowsPatcher)).Location);
            patcherAssembly.CopyTo(Path.Combine(patcherDirectory.FullName,
                Path.GetFileNameWithoutExtension(patcherAssembly.Name) + ".exe"));

            //copy dependencies
            CopyFileSameName(Assembly.GetAssembly(typeof(UpdateController)).Location, patcherDirectory);
            CopyFileSameName(Assembly.GetAssembly(typeof(JsonConvert)).Location, patcherDirectory);

            var arguments = new List<string>();

            ActionConfig = patcherConfig;
            File.WriteAllText(Path.Combine(patcherDirectory.FullName, "patcher.cfg"),
                JsonConvert.SerializeObject(this, typeof(WindowsPatcherConfig), Formatting.Indented,
                    new JsonSerializerSettings()));
            arguments.Add("/config patcher.cfg");

            if (CustomUi != null)
            {
                //copy custom ui assemblies to subfolder
                var updaterUiDirectory = patcherDirectory.CreateSubdirectory("updaterUi");
                var customUiFilename = Path.Combine(updaterUiDirectory.FullName, "CustomUi.dll");
                File.Copy(CustomUi.AssemblyPath, customUiFilename);
                if (CustomUi.RequiredLibraries?.Count > 0)
                    foreach (var requiredLibrary in CustomUi.RequiredLibraries)
                        CopyFileSameName(requiredLibrary, updaterUiDirectory);
                arguments.Add("/updaterUi updaterUi\\CustomUi.dll");
            }

            if (Language == null)
                Language = WindowsUpdaterTranslation.English;

            if (Language is ImplementedUpdaterTranslation implementedUpdaterTranslation)
                arguments.Add("/language " + implementedUpdaterTranslation.KeyName);
            else
            {
                File.WriteAllText(Path.Combine(patcherDirectory.FullName, "language.json"),
                    JsonConvert.SerializeObject(Language.Values));
                arguments.Add("/languageFile language.json");
            }

            var currentProcess = Process.GetCurrentProcess();
            arguments.Add("/hostProcess " + currentProcess.Id);

            var startInfo = new ProcessStartInfo(patcherAssembly.FullName, string.Join(" ", arguments));
            if (RunAsAdministrator)
                startInfo.Verb = "runas";

            var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Unable to start patcher process.");

            //close this application
            _applicationCloser?.ExitApplication();
        }

        private static void CopyFileSameName(string fileLocation, DirectoryInfo targetLocation)
        {
            File.Copy(fileLocation, Path.Combine(targetLocation.FullName, Path.GetFileName(fileLocation)));
        }
    }
}