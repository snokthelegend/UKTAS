using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>One route in a project. Stores the canonical script text (header and framebulks).</summary>
    [Serializable]
    public sealed class UktasBranch
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        /// <summary>Empty for the root branch.</summary>
        public string ParentId = string.Empty;
        /// <summary>Simulation time (seconds) and script tick where this branch diverged from its parent.</summary>
        public double ForkTime;
        public int ForkTick;
        /// <summary>Canonical 8-field TAS script text for the whole route.</summary>
        public string Script = string.Empty;
        /// <summary>Cached totals, refreshed whenever the script is synced. Sums of frametimes, not frame counts.</summary>
        public double TotalSeconds;
        public int TotalTicks;
    }

    /// <summary>JSON body of a .uktasproj file. Read and written with JsonUtility.</summary>
    [Serializable]
    public sealed class UktasProjectData
    {
        public int Version = 1;
        public string Name = string.Empty;
        public string Author = string.Empty;
        public int Seed;
        public string TargetLevel = string.Empty;
        public string ActiveBranchId = string.Empty;
        public List<UktasBranch> Branches = new List<UktasBranch>();
    }

    /// <summary>
    /// BXT TAS Editor 2 style workspace: many routes in one "&lt;name&gt;.uktasproj" file,
    /// with the active route compiled to "&lt;name&gt;-bridged.uktas" for the TAS player.
    ///
    /// Time comparisons use cumulative simulation time (TasScript.TotalTime), so a route
    /// with 0.001 s micro-ticks is compared fairly against one on 0.008 s ticks.
    /// </summary>
    public sealed class BranchProject
    {
        public const string ProjectExtension = ".uktasproj";
        public const string BridgedSuffix = "-bridged.uktas";

        public UktasProjectData Data { get; private set; }
        public string ProjectPath { get; private set; }

        public string BridgedPath
        {
            get
            {
                string dir = Path.GetDirectoryName(ProjectPath) ?? string.Empty;
                return Path.Combine(dir, BaseName + BridgedSuffix);
            }
        }

        public string BaseName
        {
            get { return Path.GetFileNameWithoutExtension(ProjectPath); }
        }

        public UktasBranch Active
        {
            get { return FindBranch(Data.ActiveBranchId); }
        }

        public IList<UktasBranch> Branches
        {
            get { return Data.Branches; }
        }

        BranchProject(UktasProjectData data, string projectPath)
        {
            Data = data;
            ProjectPath = projectPath;
        }

        /// <summary>Creates a project whose first branch is the given script, and writes both files.</summary>
        public static BranchProject Create(string projectPath, string name, string author, int seed, string targetLevel, TasScript initial)
        {
            if (initial == null)
                throw new ArgumentNullException("initial");
            UktasProjectData data = new UktasProjectData
            {
                Name = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(projectPath) : name,
                Author = author ?? string.Empty,
                Seed = seed,
                TargetLevel = targetLevel ?? string.Empty
            };
            UktasBranch main = new UktasBranch { Id = NewId(), Name = "Main Route" };
            data.Branches.Add(main);
            data.ActiveBranchId = main.Id;

            BranchProject project = new BranchProject(data, Path.GetFullPath(projectPath));
            project.Refresh(main, initial);
            project.Save();
            project.WriteBridged();
            return project;
        }

        public static BranchProject Open(string projectPath)
        {
            string full = Path.GetFullPath(projectPath);
            string json = File.ReadAllText(full);
            UktasProjectData data = JsonUtility.FromJson<UktasProjectData>(json);
            if (data == null || data.Branches == null || data.Branches.Count == 0)
                throw new InvalidDataException("Project has no branches: " + full);
            if (FindIn(data, data.ActiveBranchId) == null)
                data.ActiveBranchId = data.Branches[0].Id;
            return new BranchProject(data, full);
        }

        public void Save()
        {
            string json = JsonUtility.ToJson(Data, true);
            string dir = Path.GetDirectoryName(ProjectPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(ProjectPath, json);
        }

        /// <summary>Writes the active route to the bridged script the TAS player loads.</summary>
        public void WriteBridged()
        {
            UktasBranch active = Active;
            if (active == null)
                throw new InvalidOperationException("No active branch.");
            File.WriteAllText(BridgedPath, active.Script);
        }

        /// <summary>
        /// Copies the live script (the one being edited or played) into the active branch.
        /// Call before switching or saving so unsaved edits are not lost.
        /// </summary>
        public void SyncActive(TasScript live)
        {
            UktasBranch active = Active;
            if (active == null || live == null)
                return;
            Refresh(active, live);
        }

        /// <summary>
        /// Forks the active route at a tick. Frames before the tick are copied; the new
        /// branch becomes active and the bridged script is rewritten.
        /// </summary>
        public UktasBranch Fork(string name, TasScript live, int tick, out TasScript forkedScript)
        {
            if (live == null)
                throw new ArgumentNullException("live");
            UktasBranch parent = Active;
            if (parent == null)
                throw new InvalidOperationException("No active branch.");

            SyncActive(live);

            TasScript copy = TasScript.Parse(live.Serialize(), name ?? "branch");
            tick = Mathf.Clamp(tick, 0, Mathf.Max(0, copy.TotalTicks));
            copy.TruncateAtTick(tick);

            UktasBranch branch = new UktasBranch
            {
                Id = NewId(),
                Name = string.IsNullOrEmpty(name) ? "Branch " + (Data.Branches.Count + 1) : name,
                ParentId = parent.Id,
                ForkTick = tick,
                ForkTime = copy.TimeAtTick(tick)
            };
            Refresh(branch, copy);
            Data.Branches.Add(branch);
            Data.ActiveBranchId = branch.Id;

            Save();
            WriteBridged();
            forkedScript = TasScript.Parse(branch.Script, branch.Name);
            return branch;
        }

        /// <summary>
        /// Adds a branch holding a complete route (not truncated), recording forkTick as the point where
        /// it diverges from the active route. Used for optimizer results, whose frames before forkTick
        /// are identical to the parent. The new branch becomes active and the bridged script is rewritten.
        /// </summary>
        public UktasBranch AddDivergentBranch(string name, TasScript live, TasScript full, int forkTick)
        {
            if (full == null)
                throw new ArgumentNullException("full");
            UktasBranch parent = Active;
            if (parent == null)
                throw new InvalidOperationException("No active branch.");

            SyncActive(live ?? full);
            UktasBranch branch = new UktasBranch
            {
                Id = NewId(),
                Name = string.IsNullOrEmpty(name) ? "Branch " + (Data.Branches.Count + 1) : name,
                ParentId = parent.Id,
                ForkTick = Mathf.Max(0, forkTick),
                ForkTime = full.TimeAtTick(Mathf.Max(0, forkTick))
            };
            Refresh(branch, full);
            Data.Branches.Add(branch);
            Data.ActiveBranchId = branch.Id;
            Save();
            WriteBridged();
            return branch;
        }

        /// <summary>
        /// Makes another branch active. The live script is synced first. Returns the parsed
        /// script of the new branch, ready to hand to the engine.
        /// </summary>
        public TasScript SwitchTo(string branchId, TasScript live)
        {
            UktasBranch target = FindBranch(branchId);
            if (target == null)
                throw new KeyNotFoundException("No branch with id " + branchId);

            SyncActive(live);
            Data.ActiveBranchId = target.Id;
            Save();
            WriteBridged();
            return TasScript.Parse(target.Script, target.Name);
        }

        public void Rename(string branchId, string name)
        {
            UktasBranch branch = FindBranch(branchId);
            if (branch != null && !string.IsNullOrEmpty(name))
            {
                branch.Name = name;
                Save();
            }
        }

        public UktasBranch FindBranch(string id)
        {
            return FindIn(Data, id);
        }

        /// <summary>
        /// Time difference of a branch against the active one, in seconds. Positive means
        /// the branch is slower (longer). Uses cumulative simulation time.
        /// </summary>
        public double DeltaAgainstActive(UktasBranch branch)
        {
            UktasBranch active = Active;
            if (active == null || branch == null)
                return 0.0;
            return branch.TotalSeconds - active.TotalSeconds;
        }

        public static string FormatDelta(double seconds)
        {
            if (Math.Abs(seconds) < 0.0000005)
                return "±0.000000 s";
            return (seconds > 0 ? "+" : string.Empty) + seconds.ToString("0.000000", CultureInfo.InvariantCulture) + " s";
        }

        /// <summary>Refreshes a branch's stored script and cached totals from a live script.</summary>
        void Refresh(UktasBranch branch, TasScript script)
        {
            branch.Script = script.Serialize();
            branch.TotalSeconds = script.TotalTime;
            branch.TotalTicks = script.TotalTicks;
        }

        static UktasBranch FindIn(UktasProjectData data, string id)
        {
            if (string.IsNullOrEmpty(id) || data.Branches == null)
                return null;
            for (int i = 0; i < data.Branches.Count; i++)
            {
                if (data.Branches[i] != null && data.Branches[i].Id == id)
                    return data.Branches[i];
            }
            return null;
        }

        static string NewId()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
