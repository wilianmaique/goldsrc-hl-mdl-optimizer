using System;
using System.Collections.Generic;

namespace MdlOptimizer
{
    // All collapses end on an existing studio corner. The retained position, UV,
    // bone assignment and authored normal can be written to MDL without resampling.
    public static class Simplifier
    {
        struct PointKey : IEquatable<PointKey>
        {
            readonly V3 p;
            readonly int bone;

            public PointKey(V3 position, int boneId) { p = position; bone = boneId; }
            public bool Equals(PointKey other)
            {
                return bone == other.bone && p.X == other.p.X && p.Y == other.p.Y && p.Z == other.p.Z;
            }
            public override bool Equals(object obj) { return obj is PointKey && Equals((PointKey)obj); }
            public override int GetHashCode()
            {
                unchecked { return ((bone * 397 ^ p.X.GetHashCode()) * 397 ^ p.Y.GetHashCode()) * 397 ^ p.Z.GetHashCode(); }
            }
        }

        struct Attributes : IEquatable<Attributes>
        {
            readonly PointKey position, normal;
            readonly int u, t;

            public Attributes(Sub sub, Corner c)
            {
                position = Position(sub,c);
                normal = new PointKey(sub.Normals[c.N],sub.NormalBones[c.N]);
                u = c.U; t = c.T;
            }
            public bool Equals(Attributes other)
            {
                return position.Equals(other.position) && normal.Equals(other.normal) && u == other.u && t == other.t;
            }
            public override bool Equals(object obj) { return obj is Attributes && Equals((Attributes)obj); }
            public override int GetHashCode()
            {
                unchecked { return ((position.GetHashCode()*397 ^ normal.GetHashCode())*397 ^ u)*397 ^ t; }
            }
        }

        // Cyclic permutation preserves the front of the triangle. Reversing it does not.
        struct FaceKey : IEquatable<FaceKey>
        {
            readonly int a,b,c;
            public FaceKey(int x,int y,int z)
            {
                if (x <= y && x <= z) { a=x; b=y; c=z; }
                else if (y <= z) { a=y; b=z; c=x; }
                else { a=z; b=x; c=y; }
            }
            public bool Equals(FaceKey other) { return a==other.a && b==other.b && c==other.c; }
            public override bool Equals(object obj) { return obj is FaceKey && Equals((FaceKey)obj); }
            public override int GetHashCode() { unchecked { return (a*397 ^ b)*397 ^ c; } }
            public FaceKey Reverse() { return new FaceKey(a,c,b); }
            public void Mark(HashSet<int> set) { set.Add(a); set.Add(b); set.Add(c); }
        }

        static int Intern<T>(Dictionary<T,int> ids,T key)
        {
            int value;
            if (!ids.TryGetValue(key,out value)) { value=ids.Count; ids.Add(key,value); }
            return value;
        }

        static PointKey Position(Sub sub, Corner c) { return new PointKey(sub.Vertices[c.V], sub.VertexBones[c.V]); }

        static int Flags(Studio s, Mesh mesh)
        {
            int flags=0;
            foreach (int[] family in s.Skins) flags |= s.Textures[family[mesh.SkinRef]].Flags;
            return flags;
        }

        public static Dictionary<Mesh,List<Tri>> Exact(Studio s)
        {
            var result = new Dictionary<Mesh,List<Tri>>();
            foreach (Part part in s.Parts) foreach (Sub sub in part.Models) foreach (Mesh mesh in sub.Meshes)
            {
                // Material changes across skin families can introduce alpha blending.
                bool opaque = (Flags(s,mesh) & (0x10 | 0x20 | 0x40)) == 0;
                var corners = new Dictionary<Attributes,int>();
                var positions = new Dictionary<PointKey,int>();
                var surface = new Dictionary<FaceKey,FaceKey>();
                var ambiguous = new HashSet<FaceKey>();
                var used = new HashSet<FaceKey>();
                var geom = new FaceKey[mesh.Faces.Count];
                var attr = new FaceKey[mesh.Faces.Count];
                var degenerate = new bool[mesh.Faces.Count];
                for (int i=0; i<mesh.Faces.Count; i++)
                {
                    Tri t=mesh.Faces[i];
                    int a=Intern(positions,Position(sub,t.A)), b=Intern(positions,Position(sub,t.B)), c=Intern(positions,Position(sub,t.C));
                    // A coincident bind-pose point on two different bones can open during animation.
                    if (a==b || b==c || c==a) { degenerate[i]=true; continue; }
                    if (!opaque) continue;
                    FaceKey g=new FaceKey(a,b,c);
                    FaceKey f=new FaceKey(Intern(corners,new Attributes(sub,t.A)), Intern(corners,new Attributes(sub,t.B)), Intern(corners,new Attributes(sub,t.C)));
                    geom[i]=g; attr[i]=f;
                    FaceKey previous;
                    if (!surface.TryGetValue(g,out previous)) surface.Add(g,f);
                    else if (!previous.Equals(f)) ambiguous.Add(g);
                }

                var faces = new List<Tri>(mesh.Faces.Count);
                for (int i=0; i<mesh.Faces.Count; i++)
                {
                    if (degenerate[i]) continue;
                    // A coplanar overlay with different UVs or normals is order-sensitive.
                    if (opaque && !ambiguous.Contains(geom[i]) && !used.Add(attr[i])) continue;
                    faces.Add(mesh.Faces[i]);
                }
                result.Add(mesh,faces);
            }
            return result;
        }

        struct Vec
        {
            public double X,Y,Z;
            public Vec(double x,double y,double z) { X=x; Y=y; Z=z; }
            public Vec(V3 p) { X=p.X; Y=p.Y; Z=p.Z; }
            public static Vec operator -(Vec a,Vec b) { return new Vec(a.X-b.X,a.Y-b.Y,a.Z-b.Z); }
            public static Vec operator +(Vec a,Vec b) { return new Vec(a.X+b.X,a.Y+b.Y,a.Z+b.Z); }
            public static Vec operator *(Vec a,double k) { return new Vec(a.X*k,a.Y*k,a.Z*k); }
            public static double Dot(Vec a,Vec b) { return a.X*b.X+a.Y*b.Y+a.Z*b.Z; }
            public static Vec Cross(Vec a,Vec b) { return new Vec(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X); }
            public double LengthSq { get { return Dot(this,this); } }
        }

        // Symmetric 4x4 position quadric plus cross terms for five linear
        // surface attributes. The error is measured at an actual destination corner.
        sealed class Quadric
        {
            double xx,xy,xz,xw,yy,yz,yw,zz,zw,ww,weight;
            readonly double[] gradients=new double[20];
            readonly double[] channelWeights=new double[5];

            public void Plane(Vec n,double d,double area)
            {
                double x=n.X*area,y=n.Y*area,z=n.Z*area;
                xx+=x*n.X; xy+=x*n.Y; xz+=x*n.Z; xw+=x*d;
                yy+=y*n.Y; yz+=y*n.Z; yw+=y*d; zz+=z*n.Z; zw+=z*d; ww+=area*d*d;
            }
            public void Attribute(int i,Vec gradient,double offset,double area)
            {
                channelWeights[i]+=area;
                Plane(gradient,offset,area);
                int j=4*i; gradients[j]+=area*gradient.X; gradients[j+1]+=area*gradient.Y;
                gradients[j+2]+=area*gradient.Z; gradients[j+3]+=area*offset;
            }
            public void SetWeight(double area) { weight=area; }
            public void Add(Quadric q)
            {
                xx+=q.xx; xy+=q.xy; xz+=q.xz; xw+=q.xw; yy+=q.yy; yz+=q.yz;
                yw+=q.yw; zz+=q.zz; zw+=q.zw; ww+=q.ww; weight+=q.weight;
                for (int i=0;i<20;i++) gradients[i]+=q.gradients[i];
                for (int i=0;i<5;i++) channelWeights[i]+=q.channelWeights[i];
            }
            public double Error(Node v)
            {
                double x=v.P.X,y=v.P.Y,z=v.P.Z;
                double e=x*(xx*x+2*(xy*y+xz*z+xw))+y*(yy*y+2*(yz*z+yw))+z*(zz*z+2*zw)+ww;
                for (int i=0,j=0;i<5;i++,j+=4)
                {
                    double a=v.Attr[i];
                    e+=a*(channelWeights[i]*a-2*(gradients[j]*x+gradients[j+1]*y+gradients[j+2]*z+gradients[j+3]));
                }
                return Math.Max(0,e)/Math.Max(1e-24,weight);
            }
        }

        sealed class Node
        {
            public Corner Corner;
            public int Point, Bone, FrozenPass;
            public bool Locked, Sensitive;
            public RegionalSimplifier.Budget Budget;
            public Vec P;
            public readonly double[] Attr=new double[5];
            public readonly List<int> Adj=new List<int>(8);
            public readonly Quadric Q=new Quadric();
        }

        sealed class Face
        {
            public readonly int[] V=new int[3];
            public Vec InitialNormal, Normal, GradientU, GradientT;
            public double OffsetU, OffsetT;
            public bool Deleted;
            public int Color=-1;
            public Corner OriginalUV;
        }

        struct Edge : IComparable<Edge>
        {
            public int From,To;
            public double Cost;
            public int CompareTo(Edge other)
            {
                int cmp=Cost.CompareTo(other.Cost);
                return cmp!=0 ? cmp : (From!=other.From ? From.CompareTo(other.From) : To.CompareTo(other.To));
            }
        }

        static long Pair(int a,int b)
        {
            return a<b ? ((long)a<<32)|(uint)b : ((long)b<<32)|(uint)a;
        }

        sealed class World
        {
            public XForm[] Bind;
            public bool[] DetailBones;
            public double Scale;
            public Vec Origin;
            public Dictionary<PointKey,int> Points=new Dictionary<PointKey,int>();
            public HashSet<int> Shared=new HashSet<int>();
            public HashSet<int> Layered=new HashSet<int>();
        }

        static World InspectWorld(Studio studio,Dictionary<Mesh,List<Tri>> exact)
        {
            var world=new World { Bind=studio.BindPose() };
            world.DetailBones=new bool[studio.Bones.Count];
            for(int i=0;i<studio.Bones.Count;i++)
            {
                Bone bone=studio.Bones[i];
                string name=bone.Name.ToLowerInvariant();
                world.DetailBones[i]=(bone.Parent>=0 && world.DetailBones[bone.Parent]) ||
                    name.Contains("head") || name.Contains("finger") || name.Contains("hand") ||
                    name.Contains("foot") || name.Contains("eye") || name.Contains("wrist") ||
                    name.Contains("ankle") || name.Contains("thumb") || name.Contains("toe") || name.Contains("jaw");
            }
            var owners=new Dictionary<int,Mesh>();
            // Catch opposite surfaces even when their model/material or normals differ.
            var surfaces=new HashSet<FaceKey>();
            double minX=double.PositiveInfinity,minY=minX,minZ=minX,maxX=double.NegativeInfinity,maxY=maxX,maxZ=maxX;
            foreach (Part part in studio.Parts) foreach (Sub sub in part.Models)
            {
                foreach (Mesh mesh in sub.Meshes) foreach (Tri tri in exact[mesh])
                {
                    int a=0,b=0,c=0;
                    for (int j=0;j<3;j++)
                    {
                        Corner corner=tri[j]; var k=Position(sub,corner); int id=Intern(world.Points,k);
                        Mesh owner;
                        if (owners.TryGetValue(id,out owner)) { if (owner!=mesh) world.Shared.Add(id); }
                        else
                        {
                            owners.Add(id,mesh); V3 p=world.Bind[sub.VertexBones[corner.V]].Point(sub.Vertices[corner.V]);
                            minX=Math.Min(minX,p.X); minY=Math.Min(minY,p.Y); minZ=Math.Min(minZ,p.Z);
                            maxX=Math.Max(maxX,p.X); maxY=Math.Max(maxY,p.Y); maxZ=Math.Max(maxZ,p.Z);
                        }
                        if (j==0) a=id; else if(j==1) b=id; else c=id;
                    }
                    FaceKey f=new FaceKey(a,b,c);
                    if (!surfaces.Add(f) || surfaces.Contains(f.Reverse())) f.Mark(world.Layered);
                }
            }
            world.Scale=Math.Max(maxX-minX,Math.Max(maxY-minY,maxZ-minZ));
            if (world.Scale<=1e-8 || double.IsInfinity(world.Scale)) world.Scale=1;
            world.Origin=new Vec(minX,minY,minZ);
            return world;
        }

        static readonly double[] Retention={0,0.92,0.76,0.55,0.34};
        static readonly double[] Budgets={0,0.018,0.028,0.050,0.078};
        static readonly string[] ProfileKeys={"Profile.Exact","Profile.Light","Profile.Moderate","Profile.Strong","Profile.Intense"};
        public const int ModeCount=8;

        static int Strength(int level)
        {
            if(level<1 || level>ModeCount) throw new ArgumentOutOfRangeException("level");
            return 1+(level-1)%4;
        }

        public static int MilderLevel(int level)
        {
            return Strength(level)==1 ? 0 : level-1;
        }

        public static string ProfileName(int level)
        {
            return ProfileLabel(level).StableText;
        }

        internal static LocalizedText ProfileLabel(int level)
        {
            var strength = new LocalizedText(ProfileKeys[level==0 ? 0 : Strength(level)]);
            return level>4 ? new LocalizedText("Profile.Protected",strength) : strength;
        }

        public static Dictionary<Mesh,List<Tri>> Reduce(Studio s,Dictionary<Mesh,List<Tri>> exact,int level,HashSet<Mesh> protectedMeshes)
        {
            return Reduce(s,exact,level,protectedMeshes,null);
        }

        // The visual audit reports local studio vertex IDs belonging to discrepant
        // original faces. Freeze those vertices and their immediate ring, even when
        // the entire character shares a single material.
        public static Dictionary<Mesh,List<Tri>> Reduce(Studio s,Dictionary<Mesh,List<Tri>> exact,int level,
            HashSet<Mesh> protectedMeshes,Dictionary<Mesh,HashSet<int>> protectedVertices)
        {
            int strength=Strength(level);
            var world=InspectWorld(s,exact);
            var result=new Dictionary<Mesh,List<Tri>>();
            foreach (Part part in s.Parts) foreach (Sub sub in part.Models) foreach (Mesh mesh in sub.Meshes)
            {
                List<Tri> original=exact[mesh];
                int flags=Flags(s,mesh);
                if (original.Count<12 || (flags&(0x10|0x20))!=0 || (protectedMeshes!=null && protectedMeshes.Contains(mesh)))
                    result[mesh]=original;
                else
                {
                    HashSet<int> local=null;
                    if (protectedVertices!=null) protectedVertices.TryGetValue(mesh,out local);
                    result[mesh]=new Surface(s,sub,mesh,original,world,flags,local).Run(strength,level>4,original);
                }
            }
            return result;
        }

        internal static Dictionary<Mesh,List<Tri>> Regional(Studio s,Dictionary<Mesh,List<Tri>> exact,
            Dictionary<Mesh,RegionalSimplifier.MeshPlan> plans,Dictionary<Mesh,HashSet<int>> pinned)
        {
            var world=InspectWorld(s,exact);
            var result=new Dictionary<Mesh,List<Tri>>();
            foreach(Part part in s.Parts)foreach(Sub sub in part.Models)foreach(Mesh mesh in sub.Meshes) {
                var original=exact[mesh];int flags=Flags(s,mesh); HashSet<int> locked=null;
                if(pinned!=null)pinned.TryGetValue(mesh,out locked);
                if(original.Count<12 || (flags & (16|32))!=0)result[mesh]=original;
                else result[mesh]=new Surface(s,sub,mesh,original,world,flags,locked,plans[mesh]).Run(4,false,original);
            }
            return result;
        }

        sealed class Surface
        {
            readonly AdaptiveGeometry.Recorder history;
            readonly List<Node> nodes=new List<Node>();
            readonly List<Face> faces=new List<Face>();
            readonly HashSet<int> neighbors=new HashSet<int>();
            readonly HashSet<long> newEdges=new HashSet<long>();
            readonly List<Edge> candidates=new List<Edge>();
            readonly HashSet<long> pairs=new HashSet<long>();
            int active;
            readonly RegionalSimplifier.MeshPlan plan;

            public Surface(Studio studio,Sub sub,Mesh mesh,List<Tri> original,World world,int flags,HashSet<int> pinned,
                RegionalSimplifier.MeshPlan plan=null)
            {
                this.plan=plan;
                history=AdaptiveGeometry.Recording(mesh,original);
                var lookup=new Dictionary<Attributes,int>();
                var wedges=new Dictionary<int,int>();
                var incidence=new Dictionary<long,int>();
                var protectedPoints=new HashSet<int>();
                if(pinned!=null && pinned.Count>0)
                    foreach(Tri t in original)
                        if(pinned.Contains(t.A.V)||pinned.Contains(t.B.V)||pinned.Contains(t.C.V))
                            for(int j=0;j<3;j++) protectedPoints.Add(world.Points[Position(sub,t[j])]);
                int width=int.MaxValue,height=int.MaxValue;
                bool fullbright=true;
                foreach(int[] family in studio.Skins)
                {
                    Texture texture=studio.Textures[family[mesh.SkinRef]];
                    // More weight per texel when a skin family has a smaller reference texture.
                    width=Math.Min(width,texture.Width); height=Math.Min(height,texture.Height);
                    fullbright &= (texture.Flags&4)!=0;
                }
                double uvWeight=(flags&64)!=0 ? 1.15 : .85;
                double normalWeight=fullbright ? .035 : .085;
                foreach(Tri tri in original)
                {
                    Face f=new Face();
                    if(plan!=null) { f.Color=plan.Color[faces.Count];f.OriginalUV=tri.A; }
                    for(int j=0;j<3;j++)
                    {
                        Corner c=tri[j]; int id; Attributes attr=new Attributes(sub,c);
                        if(!lookup.TryGetValue(attr,out id))
                        {
                            int bone=sub.VertexBones[c.V], normalBone=sub.NormalBones[c.N];
                            int point=world.Points[Position(sub,c)];
                            Node n=new Node { Corner=c,Point=point,Bone=bone,Locked=world.Shared.Contains(point)||world.Layered.Contains(point)||protectedPoints.Contains(point)||bone!=normalBone };
                            n.P=(new Vec(world.Bind[bone].Point(sub.Vertices[c.V]))-world.Origin)*(1/world.Scale);
                            V3 normal=world.Bind[normalBone].Vector(sub.Normals[c.N]);
                            n.Attr[0]=c.U/(double)width*uvWeight; n.Attr[1]=c.T/(double)height*uvWeight;
                            n.Attr[2]=normal.X*normalWeight; n.Attr[3]=normal.Y*normalWeight; n.Attr[4]=normal.Z*normalWeight;
                            n.Sensitive=world.DetailBones[bone];
                            if(plan!=null)n.Budget=plan.Budgets[c.V];
                            id=nodes.Count; lookup.Add(attr,id); nodes.Add(n);
                            int other;
                            if(wedges.TryGetValue(point,out other)) { n.Locked=true; nodes[other].Locked=true; }
                            else wedges.Add(point,id);
                        }
                        f.V[j]=id; nodes[id].Adj.Add(faces.Count);
                    }
                    if (nodes[f.V[0]].Bone!=nodes[f.V[1]].Bone || nodes[f.V[0]].Bone!=nodes[f.V[2]].Bone)
                        foreach(int id in f.V) nodes[id].Locked=true;
                    for(int j=0;j<3;j++)
                    {
                        long pair=Pair(f.V[j],f.V[(j+1)%3]); int count;
                        incidence.TryGetValue(pair,out count); incidence[pair]=count+1;
                    }
                    InitPlane(f); faces.Add(f);
                }
                foreach(var edge in incidence) if(edge.Value!=2)
                {
                    nodes[(int)(edge.Key>>32)].Locked=true; nodes[unchecked((int)edge.Key)].Locked=true;
                }
                active=faces.Count;
            }

            void InitPlane(Face f)
            {
                Node a=nodes[f.V[0]],b=nodes[f.V[1]],c=nodes[f.V[2]];
                Vec u=b.P-a.P,v=c.P-a.P;
                Vec cross=Vec.Cross(u,v);
                double area=Math.Sqrt(cross.LengthSq);
                if(area<1e-13) { a.Locked=b.Locked=c.Locked=true; return; }
                Vec normal=cross*(1/area);
                f.Normal=f.InitialNormal=normal;
                var q=new Quadric(); q.SetWeight(area);
                q.Plane(normal,-Vec.Dot(normal,a.P),area);
                // Dual in-plane basis. For attribute A, grad(A).(b-a) == Ab-Aa.
                Vec bu=Vec.Cross(v,normal)*(1/area),bv=Vec.Cross(normal,u)*(1/area);
                for(int j=0;j<5;j++)
                {
                    Vec grad=bu*(b.Attr[j]-a.Attr[j])+bv*(c.Attr[j]-a.Attr[j]);
                    double offset=a.Attr[j]-Vec.Dot(grad,a.P);
                    if(f.Color<0 || (f.Color!=0 && j>=2))q.Attribute(j,grad,offset,area);
                    if(j==0) { f.GradientU=grad; f.OffsetU=offset; }
                    if(j==1) { f.GradientT=grad; f.OffsetT=offset; }
                }
                a.Q.Add(q); b.Q.Add(q); c.Q.Add(q);
            }

            static double UvArea(Corner a,Corner b,Corner c)
            {
                return (double)(b.U-a.U)*(c.T-a.T)-(double)(b.T-a.T)*(c.U-a.U);
            }

            bool Allowed(int from,int to,double planeLimit,double uvLimit)
            {
                neighbors.Clear(); newEdges.Clear();
                foreach(int fi in nodes[to].Adj)
                {
                    Face f=faces[fi]; if(f.Deleted) continue;
                    int a=-1,b=-1;
                    foreach(int j in f.V) if(j!=to) { neighbors.Add(j); if(a<0) a=j; else b=j; }
                    if(b>=0) newEdges.Add(Pair(a,b));
                }
                int meetings=0, common1=-1,common2=-1;
                Node target=nodes[to];
                Vec displacement=target.P-nodes[from].P;
                foreach(int fi in nodes[from].Adj)
                {
                    Face f=faces[fi]; if(f.Deleted) continue;
                    // Bound maximum deviation locally in addition to the average
                    // quadric. Small lips, glove tips and texture islands otherwise
                    // disappear in the average error of a larger cluster.
                    if(Math.Abs(Vec.Dot(displacement,f.Normal))>planeLimit) return false;
                    if(f.Color<0 && (Math.Abs(Vec.Dot(f.GradientU,target.P)+f.OffsetU-target.Attr[0])>uvLimit ||
                       Math.Abs(Vec.Dot(f.GradientT,target.P)+f.OffsetT-target.Attr[1])>uvLimit)) return false;
                    if(f.V[0]==to || f.V[1]==to || f.V[2]==to)
                    {
                        meetings++;
                        foreach(int j in f.V) if(j!=to && j!=from) { if(common1<0) common1=j; else common2=j; }
                    }
                }
                if(meetings!=2 || common1<0 || common2<0 || common1==common2) return false;
                foreach(int fi in nodes[from].Adj)
                {
                    Face f=faces[fi]; if(f.Deleted || f.V[0]==to || f.V[1]==to || f.V[2]==to) continue;
                    int slot=f.V[0]==from ? 0 : (f.V[1]==from ? 1 : 2);
                    int i=f.V[(slot+1)%3],j=f.V[(slot+2)%3];
                    if((neighbors.Contains(i) && i!=common1 && i!=common2) || (neighbors.Contains(j) && j!=common1 && j!=common2)) return false;
                    if(newEdges.Contains(Pair(i,j))) return false; // duplicate face or tetrahedron cap
                    Node a=nodes[to],b=nodes[i],c=nodes[j];
                    Vec n=Vec.Cross(b.P-a.P,c.P-a.P);
                    double size=Math.Sqrt(n.LengthSq);
                    if(Math.Abs(Vec.Dot(nodes[from].P-a.P,n))>planeLimit*size) return false;
                    if(size<1e-13 || Vec.Dot(n,f.Normal)<.66*size || Vec.Dot(n,f.InitialNormal)<.45*size) return false;
                    double before=UvArea(nodes[from].Corner,b.Corner,c.Corner);
                    double after=UvArea(a.Corner,b.Corner,c.Corner);
                    if(f.Color>=0) {if(plan.Field.Get(a.Corner,b.Corner,c.Corner,f.OriginalUV)!=f.Color)return false;}
                    else if(before!=0 && before*after<=0) return false;
                }
                return true;
            }

            void Offer(int a,int b,double maxError,double maxDetailError)
            {
                Node src=nodes[a],dst=nodes[b];
                if(src.Locked || src.Bone!=dst.Bone || a==b) return;
                double sensitivity=src.Sensitive || dst.Sensitive ? 2.65 : 1.0;
                if(plan!=null)sensitivity=1;
                // Current-cluster residual prevents catastrophic late collapses. A small
                // length bias resolves planar ties in favor of local edges.
                double cost=Math.Max(src.Q.Error(dst),dst.Q.Error(dst)) + (src.P-dst.P).LengthSq*.00018;
                cost*=sensitivity*sensitivity;
                if(src.Sensitive || dst.Sensitive) maxError=maxDetailError;
                if(plan!=null)maxError=src.Budget.Error*src.Budget.Error;
                if(cost<=maxError) candidates.Add(new Edge { From=a,To=b,Cost=cost });
            }

            void Collapse(int from,int to,int pass)
            {
                Node src=nodes[from],dst=nodes[to];
                AdaptiveGeometry.Edit edit=null;
                if(history!=null) {
                    if(!history.Started) {
                        for(int f=0;f<faces.Count;f++) {
                            int[] v=faces[f].V;
                            history.Seed(f,new Tri(nodes[v[0]].Corner,nodes[v[1]].Corner,nodes[v[2]].Corner));
                        }
                    }
                    edit=history.Begin(src.Corner.V,dst.Corner.V);
                }
                src.FrozenPass=dst.FrozenPass=pass;
                foreach(int fi in dst.Adj)
                {
                    Face f=faces[fi]; if(!f.Deleted) foreach(int i in f.V) nodes[i].FrozenPass=pass;
                }
                foreach(int fi in src.Adj)
                {
                    Face f=faces[fi]; if(f.Deleted) continue;
                    foreach(int i in f.V) nodes[i].FrozenPass=pass;
                    if(f.V[0]==to || f.V[1]==to || f.V[2]==to) {
                        if(edit!=null) history.Change(edit,fi,false,default(Tri));
                        f.Deleted=true; active--; continue;
                    }
                    for(int j=0;j<3;j++) if(f.V[j]==from) f.V[j]=to;
                    Vec p=nodes[f.V[0]].P;
                    Vec normal=Vec.Cross(nodes[f.V[1]].P-p,nodes[f.V[2]].P-p);
                    f.Normal=normal*(1/Math.Sqrt(normal.LengthSq));
                    dst.Adj.Add(fi);
                    if(edit!=null) history.Change(edit,fi,true,new Tri(nodes[f.V[0]].Corner,nodes[f.V[1]].Corner,nodes[f.V[2]].Corner));
                }
                src.Adj.Clear(); dst.Q.Add(src.Q);
            }

            public List<Tri> Run(int level,bool preserveDetails,List<Tri> original)
            {
                int target=Math.Max(8,(int)Math.Ceiling(original.Count*Retention[level]));
                if(plan!=null)target=Math.Max(8,(int)Math.Ceiling(original.Count*.14));
                double limit=Budgets[level]*Budgets[level];
                double shape=.0019*Math.Pow(1.55,level-1), detailShape=.0008*Math.Pow(1.55,level-1);
                double uv=.0065*Math.Pow(1.6,level-1), detailUv=.0025*Math.Pow(1.6,level-1);
                double detailLimit=limit;
                if(preserveDetails)
                {
                    // Spend the larger profile budget away from articulated extremities.
                    shape=.0032*Math.Pow(1.6,level-1); detailShape=.0011+.00023*(level-1);
                    uv=.009*Math.Pow(1.55,level-1); detailUv=.003+.0005*(level-1);
                    detailLimit=Math.Min(limit,.027*.027);
                }
                for(int pass=1;active>target;pass++)
                {
                    candidates.Clear(); pairs.Clear();
                    foreach(Face f in faces) if(!f.Deleted)
                        for(int j=0;j<3;j++)
                        {
                            int a=f.V[j],b=f.V[(j+1)%3];
                            if(pairs.Add(Pair(a,b))) { Offer(a,b,limit,detailLimit); Offer(b,a,limit,detailLimit); }
                        }
                    candidates.Sort();
                    int initial=active;
                    foreach(Edge edge in candidates)
                    {
                        if(nodes[edge.From].FrozenPass==pass || nodes[edge.To].FrozenPass==pass) continue;
                        bool sensitive=nodes[edge.From].Sensitive;
                        var budget=nodes[edge.From].Budget;
                        double edgeShape=budget==null?(sensitive?detailShape:shape):budget.Shape;
                        double edgeUV=budget==null?(sensitive?detailUv:uv):budget.UV;
                        if(Allowed(edge.From,edge.To,edgeShape,edgeUV))
                            Collapse(edge.From,edge.To,pass);
                        if(active<=target) break;
                    }
                    if(active==initial) break;
                }
                if(active==original.Count) return original;
                var result=new List<Tri>(active);
                foreach(Face f in faces) if(!f.Deleted)
                    result.Add(new Tri(nodes[f.V[0]].Corner,nodes[f.V[1]].Corner,nodes[f.V[2]].Corner));
                return result;
            }
        }
    }
}
