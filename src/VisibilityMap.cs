using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MdlOptimizer
{
    /// <summary>Sampled exposure, indexed in the supplied exact face lists. Unseen faces still belong to the model.</summary>
    public static class VisibilityMap
    {
        // Each independently switchable part is considered without the other
        // optional parts. An accessory in its default state must not permanently
        // hide a face that becomes visible by changing the body value.
        sealed class State
        {
            public int[] Models;
            public HashSet<Sub> Active=new HashSet<Sub>();
        }
        sealed class SampleView
        {
            public State State;
            public AuditView Camera;
            public bool[] Focus;
        }

        public static Dictionary<Mesh,HashSet<int>> Sample(Studio source,Dictionary<Mesh,List<Tri>> exact,Func<bool> cancel)
        {
            if(source==null || exact==null) throw new ArgumentNullException(source==null?"source":"exact");
            if(cancel!=null && cancel()) throw new OperationCanceledException();
            var geometry=new AuditGeometry(source);
            var output=new Dictionary<Mesh,HashSet<int>>();
            int count=geometry.Meshes.Count;
            var protectedMaterial=new bool[count+1];
            var sizes=new int[count+1];
            for(int i=1;i<=count;i++) {
                Mesh mesh=geometry.Meshes[i-1]; List<Tri> tris;
                if(!exact.TryGetValue(mesh,out tris)) throw new LocalizedText("visibility.missingMesh").AsException(message => new ArgumentException(message));
                sizes[i]=tris.Count;
                var seen=new HashSet<int>();output.Add(mesh,seen);
                foreach(int[] family in source.Skins)
                    protectedMaterial[i]|=(source.Textures[family[mesh.SkinRef]].Flags & (16|32))!=0;
                if(protectedMaterial[i]) for(int t=0;t<tris.Count;t++) seen.Add(t);
            }
            long alternatives=0;
            foreach(Part part in source.Parts)if(part.Models.Count>1)alternatives+=part.Models.Count;
            // The priority pass is optional. For unusually large collections of
            // interchangeable models, assign ordinary exposed-surface budgets to all.
            if(alternatives>128 || source.Skins.Length>128 || alternatives*count>65536) {
                foreach(var mesh in geometry.Meshes)for(int i=0;i<exact[mesh].Count;i++)output[mesh].Add(i);
                return output;
            }
            var states=States(source);
            var views=Plan(source,states);
            var facesByState=new Dictionary<State,Dictionary<Mesh,List<Tri>>>();
            var none=new List<Tri>();
            foreach(State state in states) {
                var faces=new Dictionary<Mesh,List<Tri>>(exact);
                foreach(Part part in source.Parts) foreach(Sub sub in part.Models) foreach(Mesh mesh in sub.Meshes)
                    if(!state.Active.Contains(sub) || protectedMaterial[geometry.Id[mesh]]) faces[mesh]=none;
                facesByState.Add(state,faces);
            }
            // Each worker reuses its face marks. Only the final union is shared.
            var merged=new object();
            using(var stop=new CancellationTokenSource()) {
                var parallel=new ParallelOptions {MaxDegreeOfParallelism=Math.Min(4,Environment.ProcessorCount),CancellationToken=stop.Token};
                Parallel.For(0,views.Count,parallel,
                    ()=>NewMarks(sizes),
                    delegate(int index,ParallelLoopState loop,bool[][] seen) {
                        if(cancel!=null && cancel()) {stop.Cancel();stop.Token.ThrowIfCancellationRequested();}
                        Trace(geometry,views[index],facesByState[views[index].State],seen);
                        return seen;
                    },
                    delegate(bool[][] seen) {
                        lock(merged)for(int i=1;i<=count;i++) {
                            var dest=output[geometry.Meshes[i-1]];
                            for(int t=0;t<seen[i].Length;t++) if(seen[i][t])dest.Add(t);
                        }
                    });
            }
            return output;
        }
        static bool[][] NewMarks(int[] sizes)
        {
            var flags=new bool[sizes.Length][];
            for(int i=0;i<sizes.Length;i++)flags[i]=new bool[sizes[i]];
            return flags;
        }
        static List<State> States(Studio source)
        {
            var mandatory=new State {Models=new int[source.Parts.Count]};
            foreach(Part part in source.Parts) if(part.Models.Count==1)mandatory.Active.Add(part.Models[0]);
            var result=new List<State>();
            if(mandatory.Active.Count>0)result.Add(mandatory);
            for(int p=0;p<source.Parts.Count;p++) {
                Part part=source.Parts[p];
                if(part.Models.Count==1)continue;
                for(int b=0;b<part.Models.Count;b++) {
                    Sub sub=part.Models[b];
                    if(sub.Meshes.Count==0)continue;
                    var state=new State {Models=new int[source.Parts.Count],Active=new HashSet<Sub>(mandatory.Active)};
                    state.Models[p]=b;state.Active.Add(sub);result.Add(state);
                }
            }
            return result;
        }
        static List<SampleView> Plan(Studio s,List<State> states)
        {
            var views=new List<SampleView>();
            var poses=new List<int> {-1};
            string[] hints={"idle","crouch","run","attack","jump","death"};
            for(int j=0;j<hints.Length && s.Sequences.Count>0;j++) {
                int seq=Math.Min(s.Sequences.Count-1,j*s.Sequences.Count/hints.Length);
                for(int q=0;q<s.Sequences.Count;q++) if(s.Sequences[q].Name.IndexOf(hints[j],StringComparison.OrdinalIgnoreCase)>=0) {seq=q;break;}
                if(!poses.Contains(seq))poses.Add(seq);
            }
            var head=new bool[s.Bones.Count];
            bool found=false;
            for(int b=0;b<head.Length;b++) {
                string name=s.Bones[b].Name;
                bool inherits=s.Bones[b].Parent>=0 && head[s.Bones[b].Parent];
                head[b]=inherits || name.IndexOf("head",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("skull",StringComparison.OrdinalIgnoreCase)>=0;
                found|=head[b];
            }
            for(int st=0;st<states.Count;st++) {
                State state=states[st];
                int first=views.Count;
                int n=st==0?22:12;
                int samplePoses=st==0?poses.Count:Math.Min(3,poses.Count);
                for(int p=0;p<samplePoses;p++) for(int a=0;a<n;a++) {
                    double z=1-2*(a+.5)/n;
                    double elev=Math.Asin(z)*180/Math.PI;
                    int seed=st*19+p*71+a;
                    Add(s,views,state,poses[p],seed,(a*137.50776+p*19+st*13)%360,elev,288,null);
                }
                // For unusually large sequence tables, reserve the exhaustive verification for VisualAudit.
                int sequences=Math.Min(st==0?180:8,s.Sequences.Count);
                for(int p=0;p<sequences;p++) {
                    int q=p*s.Sequences.Count/sequences;
                    Add(s,views,state,q,p+29,(p*137.50776+67)%360,(p*31%125)-62,208,null);
                }
                if(found) for(int i=0;i<(st==0?20:4);i++) {
                    int q=poses[i%poses.Count];
                    Add(s,views,state,q,31+i,(i*137.50776+17)%360,(i*29%105)-52,272,head);
                }
                // Explicitly cover each skin, including nontrivial counts relative to the pose schedule.
                foreach(var skin in MissingSkins(views,first,s.Skins.Length))
                    Add(s,views,state,poses[0],skin,(skin*53+11)%360,4,256,null);
            }
            return views;
        }
        static List<int> MissingSkins(List<SampleView> views,int first,int count)
        {
            var present=new bool[count];
            for(int i=first;i<views.Count;i++)present[views[i].Camera.Skin]=true;
            var missing=new List<int>();
            for(int s=0;s<count;s++)if(!present[s])missing.Add(s);
            return missing;
        }
        static void Add(Studio s,List<SampleView> views,State state,int sequence,int seed,double az,double el,int res,bool[] focus)
        {
            int frame=0,blend=0;
            if(sequence>=0) { Sequence q=s.Sequences[sequence];frame=(int)((19L*seed+3)%q.Frames);blend=seed%q.Blends; }
            views.Add(new SampleView {State=state,Focus=focus,Camera=new AuditView {Sequence=sequence,Frame=frame,Blend=blend,
                Models=state.Models,Skin=seed%s.Skins.Length,Azimuth=az,Elevation=el,Resolution=res}});
        }
        static void Trace(AuditGeometry geometry,SampleView view,Dictionary<Mesh,List<Tri>> faces,bool[][] seen)
        {
            Box2 full,region;
            var projected=geometry.Project(view.Camera,view.Focus,out full,out region,false);
            foreach(ScreenSub sub in projected) if(view.State.Active.Contains(sub.Sub))
                for(int j=0;j<sub.Points.Length;j++) {
                    full.Add(sub.Points[j]);
                    if(view.Focus!=null && view.Focus[sub.Sub.VertexBones[j]])region.Add(sub.Points[j]);
                }
            if(full.Empty || (view.Focus!=null && region.Empty))return;
            var frame=AuditGeometry.Frame(view.Camera.Resolution,full,region,view.Focus!=null);
            AuditGeometry.ApplyFrame(projected,frame);
            var image=geometry.Draw(view.Camera,projected,frame,faces,true);
            for(int i=0;i<image.Owner.Length;i++)
                if(image.Owner[i]!=0)seen[image.Owner[i]][image.Face[i]]=true;
            // A thin or subpixel triangle may miss all pixel centres; coincident opaque
            // layers can also tie the depth test. Probe their projected centroid against
            // the finished opaque depth buffer, treating ambiguous edge samples as visible.
            float bias=.30f/frame.Scale;
            foreach(var sub in projected) foreach(var mesh in sub.Sub.Meshes) {
                var tris=faces[mesh]; bool[] mark=seen[geometry.Id[mesh]];var points=sub.Points;
                for(int i=0;i<tris.Count;i++) {
                    if(mark[i])continue;
                    Tri t=tris[i];V3 a=points[t.A.V],b=points[t.B.V],c=points[t.C.V];
                    double area=(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
                    if(area>=0)continue;
                    int x=(int)Math.Floor((a.X+b.X+c.X)/3+.5f), y=(int)Math.Floor((a.Y+b.Y+c.Y)/3+.5f);
                    if(x<0||x>=image.Width||y<0||y>=image.Height)continue;
                    float z=(a.Z+b.Z+c.Z)/3;
                    if(z+bias>=image.Depth[(image.Height-y-1)*image.Width+x])mark[i]=true;
                }
            }
        }
    }
}
