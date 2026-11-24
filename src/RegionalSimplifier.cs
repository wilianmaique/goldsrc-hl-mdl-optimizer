using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RegionalTests")]

namespace MdlOptimizer
{
    // Budgets are in units of the original model's longest bind-pose dimension.
    // Components may span studio mesh chunks. Only their measurement is welded:
    // original corners, texture seams, bones and material assignments stay independent.
    public static class RegionalSimplifier
    {
        public const int ModeCount=4;

        struct Weld : IEquatable<Weld>
        {
            public int Bone, Skin, Alternative;
            public V3 P;
            public bool Equals(Weld o) { return Bone==o.Bone && Skin==o.Skin && Alternative==o.Alternative && P.X==o.P.X && P.Y==o.P.Y && P.Z==o.P.Z; }
            public override bool Equals(object o) {return o is Weld && Equals((Weld)o);}
            public override int GetHashCode() {unchecked{return ((((Alternative*391^Bone)*391 ^ Skin)*391 ^ P.X.GetHashCode())*391 ^P.Y.GetHashCode())*391 ^P.Z.GetHashCode();}}
        }

        sealed class Island
        {
            internal int Count, Seen;
            internal float X0=float.MaxValue, Y0=float.MaxValue, Z0=float.MaxValue;
            internal float X1=float.MinValue, Y1=float.MinValue, Z1=float.MinValue;
            internal void Include(V3 p)
            {
                X0=Math.Min(X0,p.X);Y0=Math.Min(Y0,p.Y);Z0=Math.Min(Z0,p.Z);
                X1=Math.Max(X1,p.X);Y1=Math.Max(Y1,p.Y);Z1=Math.Max(Z1,p.Z);
            }
            internal double Extent {get{return Math.Max(X1-X0,Math.Max(Y1-Y0,Z1-Z0));}}
        }

        internal sealed class Budget
        {
            internal double Shape=1, UV=1, Error=1;
        }
        internal sealed class MeshPlan
        {
            internal Budget[] Budgets;
            internal int[] Color;
            internal ColorField Field;
        }

        // Index uniform UV rectangles analytically. To ignore shading/UV on black
        // surfaces, both the old and the proposed UV triangle must lie in a
        // rectangle with exactly the same palette RGB (including a one-texel rim).
        // The same test must succeed in every active skin family.
        internal sealed class ColorField
        {
            internal sealed class Map
            {
                internal Texture Tex;
                internal int[] X,Y;
                internal int[] Palette=new int[256];
                internal Map(Texture t)
                {
                    Tex=t;
                    for(int i=0;i<256;i++) Palette[i]=(t.Palette[i*3]<<16)|(t.Palette[i*3+1]<<8)|t.Palette[i*3+2];
                    int pitch=t.Width+1;
                    X=new int[checked(pitch*(t.Height+1))];Y=new int[X.Length];
                    for(int y=0;y<t.Height;y++)
                    {
                        int hx=0,hy=0;
                        for(int x=0;x<t.Width;x++)
                        {
                            int a=y*t.Width+x,c=t.Pixels[a],j=(y+1)*pitch+x+1;
                            if(x!=0 && c!=t.Pixels[a-1]) hx++;
                            if(y!=0 && c!=t.Pixels[a-t.Width]) hy++;
                            X[j]=X[j-pitch]+hx;Y[j]=Y[j-pitch]+hy;
                        }
                    }
                }
                static int Sum(int[] v,int p,int x0,int y0,int x1,int y1)
                {
                    return v[y1*p+x1]-v[y0*p+x1]-v[y1*p+x0]+v[y0*p+x0];
                }
                internal int Get(int x0,int y0,int x1,int y1)
                {
                    int w=Tex.Width,h=Tex.Height,p=w+1;
                    if(x0<0 || y0<0 || x1>w || y1>h)return -1;
                    if(Sum(X,p,x0+1,y0,x1,y1)!=0 || Sum(Y,p,x0,y0+1,x1,y1)!=0)return -1;
                    return Tex.Pixels[y0*w+x0];
                }
            }
            internal sealed class Tables
            {
                internal Dictionary<Texture,Map> ByTexture=new Dictionary<Texture,Map>();
                // Two integral images per texture. Beyond 48 MiB the geometric/UV
                // rules still work, with no constant-color acceleration on that skin.
                internal long Remaining=48L*1024*1024;
                internal Map Resolve(Texture t)
                {
                    Map map;
                    if(ByTexture.TryGetValue(t,out map))return map;
                    long bytes=8L*(t.Width+1)*(t.Height+1);
                    if(bytes>Remaining)return null;
                    Remaining-=bytes;
                    ByTexture.Add(t,map=new Map(t));return map;
                }
            }
            readonly List<Map> maps=new List<Map>();
            internal ColorField(Studio s,Mesh m,Tables tables)
            {
                var textures=new HashSet<Texture>();
                foreach(int[] skin in s.Skins)textures.Add(s.Textures[skin[m.SkinRef]]);
                foreach(Texture t in textures)
                    if((t.Flags & (2|16|32|64))!=0 || (long)t.Width*t.Height>1048576 ||
                       (t.Name!=null && (t.Name.StartsWith("dm_base",StringComparison.OrdinalIgnoreCase) ||
                                         t.Name.StartsWith("remap",StringComparison.OrdinalIgnoreCase))))return;
                foreach(Texture t in textures) {
                    Map map=tables.Resolve(t);
                    if(map==null){maps.Clear();return;}
                    maps.Add(map);
                }
            }
            internal int Get(Corner a, Corner b, Corner c, Corner original)
            {
                if(maps.Count==0)return -1;
                int x0=Math.Min(a.U,Math.Min(b.U,c.U))-1,y0=Math.Min(a.T,Math.Min(b.T,c.T))-1;
                int x1=Math.Max(a.U,Math.Max(b.U,c.U))+2,y1=Math.Max(a.T,Math.Max(b.T,c.T))+2;
                int color=-1;
                foreach(var map in maps) {
                    int index=map.Get(x0,y0,x1,y1);
                    if(index<0 || original.U<0 || original.T<0 || original.U>=map.Tex.Width || original.T>=map.Tex.Height ||
                       map.Tex.Pixels[original.T*map.Tex.Width+original.U]!=index) return -1;
                    int rgb=map.Palette[index];if(color>=0&&color!=rgb)return -1;
                    color=rgb;
                }
                return color;
            }
        }

        sealed class Analysis
        {
            internal Dictionary<Mesh,List<Tri>> Geometry;
            internal Dictionary<Mesh,HashSet<int>> Visibility;
            internal Dictionary<Mesh,Island[]> Islands=new Dictionary<Mesh,Island[]>();
            internal Dictionary<Mesh,int[]> Colors=new Dictionary<Mesh,int[]>();
            internal Dictionary<int,ColorField> Fields=new Dictionary<int,ColorField>();
            internal int[] Anatomy;
            internal double Span;
        }
        static readonly ConditionalWeakTable<Studio,Analysis> cache=new ConditionalWeakTable<Studio,Analysis>();
        static int Root(List<int> parents,int i)
        {
            while(i!=parents[i]) {parents[i]=parents[parents[i]];i=parents[i];}return i;
        }
        static Analysis Measure(Studio s,Dictionary<Mesh,List<Tri>> exact,Dictionary<Mesh,HashSet<int>> visible)
        {
            Analysis last;
            lock(cache)if(cache.TryGetValue(s,out last) && last.Geometry==exact && last.Visibility==visible)return last;
            var a=new Analysis{Geometry=exact,Visibility=visible,Anatomy=new int[s.Bones.Count]};
            for(int i=0;i<s.Bones.Count;i++)
            {
                var b=s.Bones[i];string n=b.Name.ToLowerInvariant();int inherited=b.Parent<0?0:a.Anatomy[b.Parent];
                if(n.Contains("head")||n.Contains("skull")||n.Contains("eye")||n.Contains("jaw"))inherited=1;
                if(n.Contains("hand")||n.Contains("wrist")||n.Contains("finger")||n.Contains("thumb"))inherited=2;
                if(n.Contains("foot")||n.Contains("ankle")||n.Contains("toe"))inherited=3;
                a.Anatomy[i]=inherited;
            }
            var weld=new Dictionary<Weld,int>();var parents=new List<int>();
            var index=new Dictionary<Mesh,int[]>();
            var tables=new ColorField.Tables();
            int alternative=0;
            foreach(var p in s.Parts) foreach(var sub in p.Models)
            {
                int layer=p.Models.Count==1?0:++alternative;
                foreach(var m in sub.Meshes)
                {
                    var tris=exact[m];var ids=new int[tris.Count];index[m]=ids;
                    if(!a.Fields.ContainsKey(m.SkinRef))a.Fields.Add(m.SkinRef,new ColorField(s,m,tables));
                    var colors=new int[tris.Count];a.Colors[m]=colors;
                    for(int i=0;i<tris.Count;i++)
                    {
                        var t=tris[i];int first=0;
                        for(int j=0;j<3;j++)
                        {
                            int v=t[j].V;
                            var k=new Weld{Bone=sub.VertexBones[v],P=sub.Vertices[v],Skin=m.SkinRef,Alternative=layer};
                            int id;
                            if(!weld.TryGetValue(k,out id)){id=parents.Count;weld.Add(k,id);parents.Add(id);}
                            if(j==0)first=id;else parents[Root(parents,id)]=Root(parents,first);
                        }
                        ids[i]=first;colors[i]=a.Fields[m.SkinRef].Get(t.A,t.B,t.C,t.A);
                    }
                }
            }
            var areas=new Dictionary<int,Island>();var whole=new Island();var bind=s.BindPose();
            foreach(var p in s.Parts)foreach(var sub in p.Models)foreach(var m in sub.Meshes)
            {
                var tris=exact[m];var islands=new Island[tris.Count];a.Islands[m]=islands;
                HashSet<int> seen=null; if(visible!=null)visible.TryGetValue(m,out seen);
                for(int i=0;i<tris.Count;i++)
                {
                    int root=Root(parents,index[m][i]);Island island;
                    if(!areas.TryGetValue(root,out island))areas[root]=island=new Island();
                    islands[i]=island;island.Count++;if(seen==null||seen.Contains(i))island.Seen++;
                    for(int j=0;j<3;j++) {
                        int v=tris[i][j].V;V3 pos=bind[sub.VertexBones[v]].Point(sub.Vertices[v]);
                        island.Include(pos);whole.Include(pos);
                    }
                }
            }
            a.Span=Math.Max(.0001,whole.Extent);
            lock(cache){cache.Remove(s);cache.Add(s,a);}
            return a;
        }

        public static Dictionary<Mesh,List<Tri>> Reduce(Studio s,Dictionary<Mesh,List<Tri>> exact,
            Dictionary<Mesh,HashSet<int>> visible,int level,Dictionary<Mesh,HashSet<int>> locks)
        {
            if(level<1||level>ModeCount)throw new ArgumentOutOfRangeException("level");
            var a=Measure(s,exact,visible);var plans=new Dictionary<Mesh,MeshPlan>();
            double factor=new[]{.68,1.0,1.40,2.05}[level-1];
            foreach(var p in s.Parts)foreach(var sub in p.Models)foreach(var m in sub.Meshes)
            {
                var tris=exact[m];var islands=a.Islands[m];
                var plan=new MeshPlan{Budgets=new Budget[sub.Vertices.Length],Color=a.Colors[m],Field=a.Fields[m.SkinRef]};
                plans[m]=plan;
                HashSet<int> seen=null;if(visible!=null)visible.TryGetValue(m,out seen);
                for(int i=0;i<tris.Count;i++)
                {
                    var f=tris[i];var island=islands[i];bool hidden=seen!=null&&!seen.Contains(i);
                    double rel=island.Extent/a.Span;
                    for(int j=0;j<3;j++)
                    {
                        int id=f[j].V,cat=a.Anatomy[sub.VertexBones[id]];
                        double shape=.0042,uv=.014,error=.040;
                        if(cat==1){shape=.00115;uv=.0038;error=.022;}
                        if(cat==2){shape=.00083;uv=.003;error=.018;}
                        if(cat==3){shape=.00065;uv=.0025;error=.014;}
                        if(rel<.06 && island.Count<150){shape*=.72;error*=.84;}
                        if(hidden) {
                            // This is an error budget only. Even if the cameras missed a
                            // narrow opening, the underlying surface remains connected.
                            shape*=4.5;uv*=5;error*=4;
                            if(island.Seen*12<island.Count){shape=Math.Max(shape,.016);uv=Math.Max(uv,.06);error=Math.Max(error,.08);}
                        }
                        Budget b=plan.Budgets[id];
                        if(b==null)plan.Budgets[id]=b=new Budget();
                        b.Shape=Math.Min(b.Shape,shape*factor);b.UV=Math.Min(b.UV,uv*factor);
                        b.Error=Math.Min(b.Error,error*factor);
                    }
                }
            }
            return Simplifier.Regional(s,exact,plans,locks);
        }
    }
}
