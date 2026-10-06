namespace Affinus.AIS.FaultHandling
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Text.Json.Nodes;

    /// <summary>A workflow.json (or connections.json) read from the Logic App's file system.</summary>
    internal sealed class DefinitionFile
    {
        public DefinitionFile(string filePath, DateTime lastWriteUtc, JsonNode node)
        {
            this.FilePath = filePath;
            this.LastWriteUtc = lastWriteUtc;
            this.Node = node;
        }

        public string FilePath { get; }
        public DateTime LastWriteUtc { get; }
        public JsonNode Node { get; }
    }

    /// <summary>
    /// Finds the Logic App root folder and reads workflow definitions from it.
    /// Azure: %HOME%\site\wwwroot. Locally: the LogicApp project folder.
    /// The file text is cached against its last-write time; each call parses its own copy, because JsonNode is not thread-safe.
    /// </summary>
    internal sealed class DefinitionStore
    {
        public static readonly DefinitionStore Default = new DefinitionStore();

        private readonly ConcurrentDictionary<string, Tuple<DateTime, string>> cache =
            new ConcurrentDictionary<string, Tuple<DateTime, string>>(StringComparer.OrdinalIgnoreCase);
        private string root;

        public string Root => this.root ?? (this.root = ResolveRoot());

        public DefinitionFile LoadWorkflow(string workflowName)
        {
            var file = this.FindWorkflowFile(workflowName);
            return file == null ? null : this.LoadFile(file);
        }

        public JsonNode LoadConnections()
        {
            if (this.Root == null) return null;
            var p = Path.Combine(this.Root, "connections.json");
            return File.Exists(p) ? this.LoadFile(p).Node : null;
        }

        internal static string ResolveRoot()
        {
            // 1. Explicit override, for unusual hosting.
            var configured = Environment.GetEnvironmentVariable("FaultHandler__WorkflowRoot");
            if (IsLogicAppRoot(configured)) return configured;

            // 2. Walk up from this assembly: <root>/lib/custom/net8/FaultHandling.dll
            var dir = Path.GetDirectoryName(typeof(DefinitionStore).Assembly.Location);
            for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                if (IsLogicAppRoot(dir)) return dir;
                dir = Path.GetDirectoryName(dir);
            }

            // 3. Local runtime setting written by the VS Code extension.
            var project = Environment.GetEnvironmentVariable("ProjectDirectoryPath");
            if (IsLogicAppRoot(project)) return project;

            // 4. Azure App Service content folder.
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                var wwwroot = Path.Combine(home, "site", "wwwroot");
                if (IsLogicAppRoot(wwwroot)) return wwwroot;
            }

            var scriptRoot = Environment.GetEnvironmentVariable("AzureWebJobsScriptRoot");
            return IsLogicAppRoot(scriptRoot) ? scriptRoot : null;
        }

        private static bool IsLogicAppRoot(string dir)
        {
            return !string.IsNullOrEmpty(dir)
                && File.Exists(Path.Combine(dir, "host.json"))
                && !string.Equals(Path.GetFileName(dir), "workflow-designtime", StringComparison.OrdinalIgnoreCase);
        }

        private string FindWorkflowFile(string workflowName)
        {
            if (this.Root == null || string.IsNullOrWhiteSpace(workflowName)) return null;
            var direct = Path.Combine(this.Root, workflowName, "workflow.json");
            if (File.Exists(direct)) return direct;
            foreach (var d in Directory.EnumerateDirectories(this.Root))
            {
                if (string.Equals(Path.GetFileName(d), workflowName, StringComparison.OrdinalIgnoreCase))
                {
                    var f = Path.Combine(d, "workflow.json");
                    if (File.Exists(f)) return f;
                }
            }
            return null;
        }

        private DefinitionFile LoadFile(string path)
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            string text;
            if (this.cache.TryGetValue(path, out var hit) && hit.Item1 == stamp)
            {
                text = hit.Item2;
            }
            else
            {
                text = File.ReadAllText(path);
                this.cache[path] = Tuple.Create(stamp, text);
            }
            return new DefinitionFile(path, stamp, Json.Parse(text));
        }
    }
}
