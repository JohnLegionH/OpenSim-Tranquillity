/*
 * O-100 regression guard, the half MSBuild cannot check for itself.
 *
 * The region publish must emit every assembly whose source names IWebRtcVoiceService - the interface's
 * definition and all of its implementers are one shippable set. `AssertVoiceInterfaceAssembliesPublished`
 * in OpenSim.Server.RegionServer.csproj enforces that at publish time, but only against the list written
 * beside it. This test is what keeps that list true: it scans the source for the interface's name, maps
 * every hit to the assembly it compiles into, and requires the two sets to be equal. Adding a new
 * implementer without adding it to the list fails here; adding a stale name to the list fails here too.
 *
 * Why it matters (slice 0.8d): WebRtcVoiceServiceModule implements the interface and nothing referenced it,
 * so the publish never contained it. An operator upgrading by copying the publish output would have loaded
 * the NEW interface against the OLD implementer already in the live root - a type-load failure, with voice
 * dead rather than degraded.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoiceInterfaceAssemblyListTests
    {
        private const string Interface = "IWebRtcVoiceService";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !(Directory.Exists(Path.Combine(dir.FullName, "Source"))
                                    && Directory.Exists(Path.Combine(dir.FullName, "Addons"))))
                dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "could not find the repository root from " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        /// <summary>The assembly a source file compiles into: the nearest .csproj up the tree, by its
        /// AssemblyName when it sets one (Visibility.csproj -> VoiceVisibility) and by its file name otherwise.</summary>
        private static string AssemblyOf(string sourceFile)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile));
            while (dir != null)
            {
                string[] projects = Directory.GetFiles(dir.FullName, "*.csproj");
                if (projects.Length > 0)
                {
                    string text = File.ReadAllText(projects[0]);
                    Match m = Regex.Match(text, @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>");
                    return m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(projects[0]);
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static IEnumerable<string> SourceFilesNamingTheInterface(string root)
        {
            foreach (string area in new[] { "Addons", "Source" })
            {
                string path = Path.Combine(root, area);
                if (!Directory.Exists(path))
                    continue;
                foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                        continue;
                    if (File.ReadAllText(file).Contains(Interface))
                        yield return file;
                }
            }
        }

        private static List<string> ListedInTheRegionProject(string root, out string projectPath)
        {
            projectPath = Path.Combine(root, "Source", "OpenSim.Server.RegionServer", "OpenSim.Server.RegionServer.csproj");
            Assert.That(File.Exists(projectPath), $"the region project is missing at {projectPath}");
            return Regex.Matches(File.ReadAllText(projectPath), @"<VoiceInterfaceAssembly\s+Include=""([^""]+)""\s*/>")
                        .Select(m => m.Groups[1].Value)
                        .ToList();
        }

        [Test]
        public void TheRegionProjectListsEveryAssemblyThatNamesTheVoiceServiceInterface()
        {
            string root = RepoRoot();
            var fromSource = SourceFilesNamingTheInterface(root)
                             .Select(AssemblyOf)
                             .Where(a => a != null)
                             .ToHashSet(StringComparer.Ordinal);

            Assert.That(fromSource, Is.Not.Empty, "the scan found no source naming " + Interface);

            List<string> listed = ListedInTheRegionProject(root, out string projectPath);
            Assert.That(listed, Is.Not.Empty,
                $"{projectPath} has no <VoiceInterfaceAssembly> items; the publish-time O-100 guard has nothing to check");

            Assert.That(listed.OrderBy(x => x, StringComparer.Ordinal),
                Is.EqualTo(fromSource.OrderBy(x => x, StringComparer.Ordinal)),
                "the VoiceInterfaceAssembly list and the assemblies whose source names " + Interface + " have diverged. "
                + "Listed: " + string.Join(", ", listed.OrderBy(x => x, StringComparer.Ordinal))
                + " | from source: " + string.Join(", ", fromSource.OrderBy(x => x, StringComparer.Ordinal)));
        }

        [Test]
        public void EveryListedAssemblyIsReachableFromTheRegionProjectsReferences()
        {
            string root = RepoRoot();
            List<string> listed = ListedInTheRegionProject(root, out string projectPath);

            // Walk the ProjectReference graph from the region project; an assembly the graph never reaches
            // cannot be in the publish, whatever the list says.
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(projectPath);
            while (queue.Count > 0)
            {
                string project = queue.Dequeue();
                string text = File.ReadAllText(project);
                Match name = Regex.Match(text, @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>");
                if (!reached.Add(name.Success ? name.Groups[1].Value : Path.GetFileNameWithoutExtension(project)))
                    continue;
                foreach (Match m in Regex.Matches(text, @"<ProjectReference\s+Include=""([^""]+)""\s*/>"))
                {
                    string referenced = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project),
                                                                      m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(referenced))
                        queue.Enqueue(referenced);
                }
            }

            foreach (string assembly in listed)
                Assert.That(reached, Does.Contain(assembly),
                    $"{assembly} is listed for the O-100 publish guard but nothing in the region project's "
                    + "ProjectReference graph pulls it in, so the publish will not contain it (this is exactly the "
                    + "WebRtcVoiceServiceModule bug)");
        }
    }
}
