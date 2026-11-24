using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PatchAtlasTests")]

namespace MdlOptimizer
{
    // Two triangulations can exchange a surface region if its directed boundary
    // is identical. A corner includes the studio vertex, normal and both UVs.
    internal sealed class PatchAtlas
    {
        internal sealed class Patch
        {
            internal Mesh Mesh;
            internal List<int> Before;
            internal List<Tri> After;
            internal HashSet<int> Vertices;
            internal int Saving { get { return Before.Count-After.Count; } }
        }

        readonly Dictionary<Mesh,List<Tri>> baseline;
        readonly Dictionary<Mesh,List<Patch>> byMesh=new Dictionary<Mesh,List<Patch>>();
        internal readonly List<Patch> Patches=new List<Patch>();

        struct FaceId : IEquatable<FaceId>
        {
            readonly int a,b,c;
            internal FaceId(int x,int y,int z)
            {
                if(x<=y && x<=z) {a=x;b=y;c=z;}
                else if(y<=z) {a=y;b=z;c=x;}
                else {a=z;b=x;c=y;}
            }
            public bool Equals(FaceId o) { return a==o.a && b==o.b && c==o.c; }
            public override bool Equals(object o) { return o is FaceId && Equals((FaceId)o); }
            public override int GetHashCode() { unchecked {return (a*397^b)*397^c;} }
        }
        sealed class Side
        {
            internal readonly List<Tri> Faces;
            internal readonly int[] CornerIds,Parent;
            internal readonly bool[] Common;
            internal readonly Dictionary<long,int> First=new Dictionary<long,int>();
            internal Side(List<Tri> faces,Dictionary<Corner,int> ids)
            {
                Faces=faces; CornerIds=new int[3*faces.Count]; Parent=new int[faces.Count]; Common=new bool[faces.Count];
                for(int i=0;i<faces.Count;i++) {
                    Parent[i]=i;
                    for(int j=0;j<3;j++) {
                        Corner corner=faces[i][j];int id;
                        if(!ids.TryGetValue(corner,out id)) {id=ids.Count;ids.Add(corner,id);}
                        CornerIds[3*i+j]=id;
                    }
                }
            }
            internal FaceId Id(int i) { return new FaceId(CornerIds[3*i],CornerIds[3*i+1],CornerIds[3*i+2]); }
            internal void IndexEdges()
            {
                for(int i=0;i<Faces.Count;i++) if(!Common[i]) for(int j=0;j<3;j++) {
                    long e=Edge(CornerIds[3*i+j],CornerIds[3*i+(j+1)%3]);
                    if(!First.ContainsKey(e))First.Add(e,i);
                }
            }
            int Root(int i)
            {
                while(Parent[i]!=i) {Parent[i]=Parent[Parent[i]];i=Parent[i];}
                return i;
            }
            internal List<List<int>> Regions(Side other)
            {
                for(int i=0;i<Faces.Count;i++) if(!Common[i]) for(int j=0;j<3;j++) {
                    long e=Edge(CornerIds[3*i+j],CornerIds[3*i+(j+1)%3]);
                    if(!other.First.ContainsKey(e)) Parent[Root(i)]=Root(First[e]);
                }
                var groups=new Dictionary<int,List<int>>();
                for(int i=0;i<Faces.Count;i++) if(!Common[i]) {
                    int root=Root(i);List<int> group;
                    if(!groups.TryGetValue(root,out group))groups.Add(root,group=new List<int>());
                    group.Add(i);
                }
                return groups.Values.OrderBy(g=>g[0]).ToList();
            }
            internal string Boundary(List<int> region)
            {
                // Link the two corners at each end of every paired interior edge.
                // Disjoint incident fans at one corner reveal a pinched surface.
                int size=3*region.Count;
                var link=new int[size];
                var corners=new int[size];
                for(int i=0;i<size;i++) {
                    link[i]=i;
                    corners[i]=CornerIds[3*region[i/3]+i%3];
                }
                var edges=new Dictionary<long,int>();
                for(int i=0;i<size;i++) {
                    int j=NextCorner(i),a=corners[i],b=corners[j],first;
                    if(a==b)return "";
                    long edge=Edge(a,b);
                    if(!edges.TryGetValue(edge,out first)) {edges.Add(edge,i);continue;}
                    if(first<0 || corners[first]!=b)return "";
                    link[Fan(link,i)]=Fan(link,NextCorner(first));
                    link[Fan(link,j)]=Fan(link,first);
                    edges[edge]=-1;
                }
                var fans=new Dictionary<int,int>();
                for(int i=0;i<size;i++) {
                    int root=Fan(link,i),previous;
                    if(!fans.TryGetValue(corners[i],out previous))fans.Add(corners[i],root);
                    else if(previous!=root)return "";
                }
                var boundary=edges.Where(p=>p.Value>=0).OrderBy(p=>p.Key).ToArray();
                if(boundary.Length==0)return "";
                // Equal contours alone do not distinguish a disk from a surface
                // with a handle. Match Euler characteristic for these connected,
                // orientable manifold regions before comparing directed edges.
                var key=new StringBuilder().Append(fans.Count-edges.Count+region.Count).Append(':');
                foreach(var edge in boundary) {
                    int i=edge.Value;
                    key.Append(edge.Key).Append('/').Append(corners[i]<corners[NextCorner(i)]?1:-1).Append(',');
                }
                return key.ToString();
            }
            static int NextCorner(int i) { return i%3==2?i-2:i+1; }
            static int Fan(int[] link,int i)
            {
                while(link[i]!=i) {link[i]=link[link[i]];i=link[i];}
                return i;
            }
        }
        static long Edge(int a,int b)
        {
            return a<b?((long)a<<32)|(uint)b:((long)b<<32)|(uint)a;
        }
        internal PatchAtlas(Dictionary<Mesh,List<Tri>> from,Dictionary<Mesh,List<Tri>> to)
        {
            baseline=from;
            foreach(var entry in from) {
                var other=to[entry.Key];if(Object.ReferenceEquals(entry.Value,other))continue;
                Index(entry.Key,entry.Value,other);
            }
        }
        void Index(Mesh mesh,List<Tri> source,List<Tri> proposal)
        {
            var ids=new Dictionary<Corner,int>();var a=new Side(source,ids);var b=new Side(proposal,ids);
            var known=new Dictionary<FaceId,Queue<int>>();
            for(int i=0;i<a.Faces.Count;i++) {
                var key=a.Id(i);Queue<int> list;
                if(!known.TryGetValue(key,out list))known.Add(key,list=new Queue<int>());
                list.Enqueue(i);
            }
            for(int i=0;i<b.Faces.Count;i++) {
                Queue<int> list;
                if(known.TryGetValue(b.Id(i),out list)&&list.Count>0){a.Common[list.Dequeue()]=true;b.Common[i]=true;}
            }
            a.IndexEdges();b.IndexEdges();
            var oldRegions=a.Regions(b);var newRegions=b.Regions(a);
            var oldGroups=oldRegions.GroupBy(a.Boundary).ToDictionary(g=>g.Key,g=>g.ToList());
            var newGroups=newRegions.GroupBy(b.Boundary).ToDictionary(g=>g.Key,g=>g.ToList());
            var picked=new List<Patch>();
            foreach(var item in oldGroups) {
                List<List<int>> match;
                // Closed, ambiguous or non-matching islands are kept from the baseline.
                if(item.Key.Length==0 || item.Value.Count!=1 || !newGroups.TryGetValue(item.Key,out match) || match.Count!=1)continue;
                var before=item.Value[0];var after=match[0];
                if(after.Count>=before.Count)continue;
                var vertices=new HashSet<int>();
                foreach(int i in before)for(int j=0;j<3;j++) vertices.Add(source[i][j].V);
                foreach(int i in after)for(int j=0;j<3;j++) vertices.Add(proposal[i][j].V);
                picked.Add(new Patch{Mesh=mesh,Before=before,After=after.Select(i=>proposal[i]).ToList(),Vertices=vertices});
            }
            if(picked.Count>0) {picked.Sort((p,q)=>p.Before[0].CompareTo(q.Before[0]));byMesh.Add(mesh,picked);Patches.AddRange(picked);}
        }
        internal Dictionary<Mesh,List<Tri>> Apply(HashSet<Patch> enabled)
        {
            var output=new Dictionary<Mesh,List<Tri>>(baseline);
            foreach(var entry in byMesh) {
                var old=baseline[entry.Key];var skip=new bool[old.Count];var insert=new Dictionary<int,List<Tri>>();
                foreach(var p in entry.Value) if(enabled.Contains(p)) {
                    foreach(int i in p.Before)skip[i]=true;
                    insert.Add(p.Before[0],p.After);
                }
                if(insert.Count==0)continue;
                var final=new List<Tri>(old.Count);
                for(int i=0;i<old.Count;i++) {
                    if(!skip[i])final.Add(old[i]);
                    else {List<Tri> replacement;if(insert.TryGetValue(i,out replacement))final.AddRange(replacement);}
                }
                output[entry.Key]=final;
            }
            return output;
        }
    }
}
