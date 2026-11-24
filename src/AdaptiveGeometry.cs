using System;
using System.Collections.Generic;

namespace MdlOptimizer
{
    // Keep vertex splits implicit: the input slot and each collapse's replacement
    // corners describe both the high-resolution and simplified triangle at that slot.
    public sealed class AdaptiveGeometry
    {
        [ThreadStatic] static AdaptiveGeometry capture;

        internal struct Replacement
        {
            internal int Slot;
            internal bool Present;
            internal Tri Face;
        }

        internal sealed class Edit
        {
            internal int From,To;
            internal readonly List<Replacement> Changes=new List<Replacement>();
            internal readonly HashSet<int> Parents=new HashSet<int>();
            internal bool Restored;
        }

        internal sealed class Track
        {
            internal readonly List<Tri> Original;
            internal readonly Tri[] Initial,Current;
            internal readonly List<Edit> Edits=new List<Edit>();
            internal readonly int[] Last;
            internal readonly Dictionary<int,List<int>> RemovedAt=new Dictionary<int,List<int>>();
            Dictionary<Tri,Queue<int>> slots;
            Dictionary<int,HashSet<int>> aliases;

            internal Track(List<Tri> original)
            {
                Original=original;
                Initial=original.ToArray();
                Current=original.ToArray();
                Last=new int[original.Count];
            }

            internal int[] Match(List<Tri> faces)
            {
                if(Object.ReferenceEquals(Original,faces))return null;
                if(slots==null) {
                    slots=new Dictionary<Tri,Queue<int>>();
                    for(int i=0;i<Original.Count;i++) {
                        Queue<int> ids;
                        if(!slots.TryGetValue(Original[i],out ids))slots[Original[i]]=ids=new Queue<int>();
                        ids.Enqueue(i);
                    }
                }
                var map=new int[faces.Count];
                for(int i=0;i<map.Length;i++) {
                    Queue<int> ids;
                    if(!slots.TryGetValue(faces[i],out ids)||ids.Count==0)
                        throw new LocalizedText("geometry.regionFace").AsException(message => new InvalidOperationException(message));
                    map[i]=ids.Dequeue();
                }
                return map;
            }

            internal void AttachDeleted(int vertex,int id)
            {
                List<int> parents;
                if(!RemovedAt.TryGetValue(vertex,out parents))RemovedAt[vertex]=parents=new List<int>();
                if(parents.Count==0||parents[parents.Count-1]!=id)parents.Add(id);
            }

            internal void AddDependencies(Edit edit,int vertex)
            {
                List<int> previous;
                if(RemovedAt.TryGetValue(vertex,out previous))foreach(int id in previous)edit.Parents.Add(id);
            }

            internal void Seed(int i,Tri normalized)
            {
                Initial[i]=Current[i]=normalized;
                for(int j=0;j<3;j++) {
                    int source=Original[i][j].V,target=normalized[j].V;
                    if(source==target)continue;
                    if(aliases==null)aliases=new Dictionary<int,HashSet<int>>();
                    HashSet<int> targets;
                    if(!aliases.TryGetValue(source,out targets))aliases.Add(source,targets=new HashSet<int>());
                    targets.Add(target);
                }
            }

            internal HashSet<int> Resolve(HashSet<int> suspects)
            {
                if(aliases==null)return suspects;
                var result=new HashSet<int>(suspects);
                foreach(int v in suspects) {
                    HashSet<int> targets;
                    if(aliases.TryGetValue(v,out targets))result.UnionWith(targets);
                }
                return result;
            }

            internal List<Tri> Rebuild()
            {
                // Full reversal also recovers the input's exact indexing when
                // distinct MDL indices represented identical vertex attributes.
                if(Edits.TrueForAll(e=>e.Restored))return Original;
                Tri[] faces=(Tri[])Initial.Clone();
                bool[] removed=new bool[faces.Length];
                foreach(Edit edit in Edits)if(!edit.Restored)
                    foreach(Replacement r in edit.Changes) {
                        faces[r.Slot]=r.Face;
                        removed[r.Slot]=!r.Present;
                    }
                var result=new List<Tri>(faces.Length);
                for(int i=0;i<faces.Length;i++)if(!removed[i])result.Add(faces[i]);
                return result;
            }
        }

        internal sealed class Recorder
        {
            readonly Track track;
            readonly int[] slots;
            internal bool Started { get; private set; }
            internal Recorder(Track track,int[] slots) { this.track=track;this.slots=slots; }
            int Slot(int face) { return slots==null?face:slots[face]; }

            internal void Seed(int face,Tri normalized) { track.Seed(Slot(face),normalized); }

            internal Edit Begin(int from,int to)
            {
                Started=true;
                var edit=new Edit{From=from,To=to};
                // Restoring an earlier deletion can make an old face incident to this
                // edge again. Its later collapse must then also be restored.
                track.AddDependencies(edit,from);
                track.AddDependencies(edit,to);
                track.Edits.Add(edit);
                return edit;
            }

            internal void Change(Edit edit,int local,bool present,Tri after)
            {
                int i=Slot(local),id=track.Edits.Count-1;
                int previous=track.Last[i];
                if(previous!=0&&previous-1!=id)edit.Parents.Add(previous-1);
                Tri before=track.Current[i];
                if(!present) {
                    track.AttachDeleted(before.A.V,id);
                    track.AttachDeleted(before.B.V,id);
                    track.AttachDeleted(before.C.V,id);
                }
                edit.Changes.Add(new Replacement{Slot=i,Present=present,Face=after});
                track.Last[i]=id+1; track.Current[i]=after;
            }
        }

        readonly Dictionary<Mesh,Track> tracks=new Dictionary<Mesh,Track>();
        readonly Dictionary<Mesh,List<Tri>> originals;
        public Dictionary<Mesh,List<Tri>> Faces {get;private set;}
        public int LastRestored {get;private set;}
        AdaptiveGeometry(Dictionary<Mesh,List<Tri>> originals) { this.originals=originals; }

        public static AdaptiveGeometry Capture(Dictionary<Mesh,List<Tri>> exact,Func<Dictionary<Mesh,List<Tri>>> generate)
        {
            if(capture!=null)throw new LocalizedText("geometry.nestedCapture").AsException(message => new InvalidOperationException(message));
            var plan=new AdaptiveGeometry(exact);
            capture=plan;
            try { plan.Faces=generate(); }
            finally { capture=null; }
            return plan;
        }

        public static AdaptiveGeometry Trace(Studio s,Dictionary<Mesh,List<Tri>> exact,int level)
        {
            return Capture(exact,()=>Simplifier.Reduce(s,exact,level,null));
        }

        internal static Recorder Recording(Mesh mesh,List<Tri> faces)
        {
            if(capture==null)return null;
            Track track;
            if(!capture.tracks.TryGetValue(mesh,out track))
                capture.tracks[mesh]=track=new Track(capture.originals[mesh]);
            return new Recorder(track,track.Match(faces));
        }

        // A rejected view supplies original vertex IDs. Revert the touching
        // collapses and any later collapses that rely on the edited triangle rings.
        // Independent operations outside that dependency closure are preserved.
        public Dictionary<Mesh,List<Tri>> Repair(Dictionary<Mesh,HashSet<int>> suspects)
        {
            LastRestored=0;
            var next=new Dictionary<Mesh,List<Tri>>(Faces);
            foreach(var region in suspects) {
                Track track;
                if(!tracks.TryGetValue(region.Key,out track)||region.Value.Count==0)continue;
                var vertices=track.Resolve(region.Value);
                bool changed=false;
                for(int i=0;i<track.Edits.Count;i++) {
                    Edit edit=track.Edits[i];
                    if(edit.Restored)continue;
                    bool restore=vertices.Contains(edit.From)||vertices.Contains(edit.To);
                    if(!restore) foreach(int p in edit.Parents) if(track.Edits[p].Restored) { restore=true;break; }
                    if(!restore) continue;
                    edit.Restored=true;LastRestored++;changed=true;
                }
                if(changed)next[region.Key]=track.Rebuild();
            }
            if(LastRestored==0)return null;
            Faces=next;
            return next;
        }
    }
}
