using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MdlOptimizer
{
    // Improve an already verified result. Every challenger must reduce its triangle
    // count and pass the same original-vs-exported audit before replacing it.
    internal sealed class SurfaceSearch
    {
        readonly Studio source;
        readonly VisualAudit quick,complete,detail;
        readonly double minSilhouette,maxColor;
        readonly string destination,internalName,trialPath;
        readonly Func<bool> cancel;
        readonly Action<int,LocalizedText> progress;
        readonly List<object> trials;
        readonly Dictionary<Mesh,HashSet<int>> sensitive=new Dictionary<Mesh,HashSet<int>>();
        internal Dictionary<Mesh,List<Tri>> Best;
        internal int Count,DefaultCount;
        internal VisualResult Visual,Detail;
        internal string Selected;
        internal int VisibleFaces;

        internal SurfaceSearch(Studio source,VisualAudit quick,VisualAudit complete,VisualAudit detail,
            string destination,string internalName,Func<bool> cancel,Action<int,LocalizedText> progress,
            List<object> trials,Dictionary<Mesh,List<Tri>> initial,VisualResult visual,string profile,int defaultCount)
        {
            this.source=source;this.quick=quick;this.complete=complete;this.detail=detail;this.destination=destination;this.internalName=internalName;
            this.cancel=cancel;this.progress=progress;this.trials=trials;
            trialPath=DiskPath.Full(Path.Combine(Path.GetDirectoryName(destination),"ensaio-"+Guid.NewGuid().ToString("N")+".mdl"));
            Best=initial;Count=FaceCount(initial);DefaultCount=defaultCount;Visual=visual;Selected=profile;
            minSilhouette=Math.Max(.989,visual.Silhouette-.0025);
            maxColor=Math.Min(3.3,visual.MeanColor+.75);
        }
        internal object Budget {get{return new{min_silhouette_iou=minSilhouette,max_mean_rgb_error_255=maxColor};}}
        static int FaceCount(Dictionary<Mesh,List<Tri>> faces) {return faces.Values.Sum(t=>t.Count);}
        void Check() {if(cancel())throw new OperationCanceledException();}
        // Return feedback only for a rejection. A successful trial becomes the new baseline.
        VisualResult Evaluate(LocalizedText label,Dictionary<Mesh,List<Tri>> faces)
        {
            Check();
            int n=FaceCount(faces);
            if(n>=Count)return null;
            progress(83,new LocalizedText("Progress.Triangles",label,n));
            var screen=quick.Compare(faces,cancel,null);
            VisualResult disk=null,extra=null,feedback=screen;
            bool accepted=false,withinBudget=false;
            if(screen.Passed) {
                source.Save(trialPath,faces,false,internalName);
                try {
                    var saved=Studio.Load(trialPath);source.VerifyMetadata(saved);
                    if(saved.TriangleCount!=n)throw LocalizedText.InvalidData("Core.RegionalCountMismatch");
                    disk=complete.CompareSaved(saved,cancel,null);
                    feedback=disk;
                    withinBudget=disk.Silhouette>=minSilhouette && disk.MeanColor<=maxColor;
                    if(disk.Passed && !withinBudget)complete.LocateDifferences(disk,faces);
                    if(disk.Passed && withinBudget) {
                        extra=detail.CompareSaved(saved,cancel,null);
                        feedback=extra;
                        accepted=extra.Passed;
                    }
                    if(accepted) {
                        if(File.Exists(destination))File.Delete(destination);
                        File.Move(trialPath,destination);
                        Best=faces;Count=n;DefaultCount=Optimizer.DefaultCount(saved);Selected=label.StableText;Visual=disk;Detail=extra;
                    }
                } finally { if(File.Exists(trialPath)) File.Delete(trialPath); }
            }
            trials.Add(new{profile=label.StableText,triangles=n,screen=Optimizer.Metrics(screen),export=disk==null?null:Optimizer.Metrics(disk),
                detail=extra==null?null:Optimizer.Metrics(extra),within_fidelity_budget=disk==null?(bool?)null:withinBudget,accepted=accepted});
            if(!accepted)foreach(var pair in feedback.SuspectVertices) {
                HashSet<int> vertices;
                if(!sensitive.TryGetValue(pair.Key,out vertices))sensitive.Add(pair.Key,vertices=new HashSet<int>());
                vertices.UnionWith(pair.Value);
            }
            return accepted?null:feedback;
        }
        void Combine(LocalizedText label,Dictionary<Mesh,List<Tri>> proposal,int rounds)
        {
            Check();
            var atlas=new PatchAtlas(Best,proposal);
            var active=new HashSet<PatchAtlas.Patch>(atlas.Patches);
            for(int i=0;i<rounds && active.Count>0;i++) {
                var result=Evaluate(new LocalizedText("Profile.Contours",label,i+1),atlas.Apply(active));
                if(result==null)break;
                int removed=active.RemoveWhere(p=>{
                    HashSet<int> vertices;
                    return result.SuspectVertices.TryGetValue(p.Mesh,out vertices) && vertices.Overlaps(p.Vertices);
                });
                if(removed==0)removed=active.RemoveWhere(p=>result.SuspectMeshes.Contains(p.Mesh));
                if(removed==0)break;
            }
        }
        void TryHistory(LocalizedText label,AdaptiveGeometry history,int rounds)
        {
            for(int i=0;i<rounds;i++) {
                var rejected=Evaluate(new LocalizedText("Profile.Iteration",label,i+1),history.Faces);
                if(rejected==null || history.Repair(rejected.SuspectVertices)==null)break;
            }
        }
        // A rejected patch group can contain distant changes that work on their own.
        // Start with the whole set. Bisect only rejected groups, keeping each
        // approved subset in the next exported trial. Bound work on large models.
        void Bisect(LocalizedText label,Dictionary<Mesh,List<Tri>> proposal)
        {
            var atlas=new PatchAtlas(Best,proposal);
            var kept=new HashSet<PatchAtlas.Patch>();
            var pending=new Queue<List<PatchAtlas.Patch>>();
            if(atlas.Patches.Count>0)pending.Enqueue(atlas.Patches);
            for(int i=0;i<15 && pending.Count>0;i++) {
                var group=pending.Dequeue();
                var active=new HashSet<PatchAtlas.Patch>(kept);active.UnionWith(group);
                int previous=Count;
                Evaluate(new LocalizedText("Profile.Group",label,i+1),atlas.Apply(active));
                if(Count<previous) {kept=active;continue;}
                if(group.Count<2)continue;
                int half=group.Count/2;
                pending.Enqueue(group.GetRange(0,half));pending.Enqueue(group.GetRange(half,group.Count-half));
            }
        }
        internal void Run(Dictionary<Mesh,List<Tri>> exact,VisualResult exactView,VisualResult exactDetail,int exactDefault,
            Dictionary<int,Dictionary<Mesh,List<Tri>>> legacy,bool thorough)
        {
            var initial=Best;
            Detail=Count==FaceCount(exact)?exactDetail:detail.CompareSaved(Studio.Load(destination),cancel,null);
            if(!Detail.Passed) {
                trials.Add(new{profile=Selected,triangles=Count,detail=Optimizer.Metrics(Detail),accepted=false});
                Best=exact;Visual=exactView;Detail=exactDetail;Count=FaceCount(exact);DefaultCount=exactDefault;Selected="exato";
            }
            progress(81,new LocalizedText("Progress.MeasurePoses"));
            var visible=VisibilityMap.Sample(source,exact,cancel);
            VisibleFaces=visible.Values.Sum(s=>s.Count);
            var proposals=new List<KeyValuePair<LocalizedText,Dictionary<Mesh,List<Tri>>>>();
            AdaptiveGeometry regional=null;
            for(int level=1;level<=RegionalSimplifier.ModeCount;level++) {
                Check();
                Dictionary<Mesh,List<Tri>> candidate;
                if(level==RegionalSimplifier.ModeCount) {
                    regional=AdaptiveGeometry.Capture(exact,()=>RegionalSimplifier.Reduce(source,exact,visible,level,null));
                    candidate=regional.Faces;
                } else candidate=RegionalSimplifier.Reduce(source,exact,visible,level,null);
                proposals.Add(new KeyValuePair<LocalizedText,Dictionary<Mesh,List<Tri>>>(new LocalizedText("Profile.Visibility",level),candidate));
            }
            foreach(int level in new[]{4,8})
                proposals.Add(new KeyValuePair<LocalizedText,Dictionary<Mesh,List<Tri>>>(Simplifier.ProfileLabel(level),legacy[level]));
            if(initial!=Best)proposals.Add(new KeyValuePair<LocalizedText,Dictionary<Mesh,List<Tri>>>(new LocalizedText("Profile.ContourRepair"),initial));
            // A global profile can be heavier but still contain a lighter independent patch.
            foreach(var p in proposals.OrderBy(p=>FaceCount(p.Value))) {
                if(p.Key.Key=="Profile.Visibility") Evaluate(p.Key,p.Value);
                Combine(p.Key,p.Value,thorough?12:5);
            }
            int rounds=thorough?12:5;
            TryHistory(new LocalizedText("Profile.Rollback"),regional,rounds);
            foreach(int level in thorough?new[]{4,8,3,7}:new[]{4}) {
                Check();
                TryHistory(new LocalizedText("Profile.Refinement",level),AdaptiveGeometry.Trace(source,Best,level),rounds);
            }
            if(thorough) {
                // Preserve corners repeatedly implicated by image comparisons.
                // Re-run on the current approved surface so errors do not accumulate
                // near those corners while independent areas can still be lightened.
                foreach(int level in new[]{4,2}) {
                    Check();
                    var pinned=Simplifier.Reduce(source,Best,level,null,sensitive);
                    Bisect(new LocalizedText("Profile.RefineProtected",level),pinned);
                }
            }
            if(Count==FaceCount(exact))
                foreach(var p in legacy.OrderBy(p=>FaceCount(p.Value)))
                    Evaluate(Simplifier.ProfileLabel(p.Key),p.Value);
        }
    }
}
