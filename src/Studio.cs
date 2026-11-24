using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace MdlOptimizer
{
    public struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public struct XForm
    {
        float a, b, c, d, e, f, g, h, i;
        V3 translation;
        internal static XForm Local(float x, float y, float z, float rx, float ry, float rz)
        {
            double cx = Math.Cos(rx), sx = Math.Sin(rx), cy = Math.Cos(ry), sy = Math.Sin(ry);
            double cz = Math.Cos(rz), sz = Math.Sin(rz);
            return new XForm {
                a=(float)(cz*cy), b=(float)(cz*sy*sx-sz*cx), c=(float)(cz*sy*cx+sz*sx),
                d=(float)(sz*cy), e=(float)(sz*sy*sx+cz*cx), f=(float)(sz*sy*cx-cz*sx),
                g=(float)-sy, h=(float)(cy*sx), i=(float)(cy*cx), translation=new V3(x,y,z)
            };
        }
        public V3 Vector(V3 v) { return new V3(a*v.X+b*v.Y+c*v.Z, d*v.X+e*v.Y+f*v.Z, g*v.X+h*v.Y+i*v.Z); }
        public V3 Point(V3 v) { V3 w=Vector(v); return new V3(w.X+translation.X,w.Y+translation.Y,w.Z+translation.Z); }
        internal XForm Then(XForm child)
        {
            V3 u=Vector(new V3(child.a,child.d,child.g));
            V3 v=Vector(new V3(child.b,child.e,child.h));
            V3 w=Vector(new V3(child.c,child.f,child.i));
            return new XForm { a=u.X,d=u.Y,g=u.Z,b=v.X,e=v.Y,h=v.Z,c=w.X,f=w.Y,i=w.Z,translation=Point(child.translation) };
        }
    }

    public struct Corner : IEquatable<Corner>
    {
        public int V, N, U, T;
        public Corner(int v,int n,int u,int t) { V=v; N=n; U=u; T=t; }
        public bool Equals(Corner o) { return V==o.V && N==o.N && U==o.U && T==o.T; }
        public override bool Equals(object o) { return o is Corner && Equals((Corner)o); }
        public override int GetHashCode() { unchecked { return ((V*397^N)*397^U)*397^T; } }
    }

    public struct Tri : IEquatable<Tri>
    {
        public Corner A, B, C;
        public Tri(Corner a, Corner b, Corner c) { A=a; B=b; C=c; }
        public Corner this[int i] { get { if(i==0)return A; if(i==1)return B; if(i==2)return C; throw new IndexOutOfRangeException(); } }
        public bool Equals(Tri o) { return A.Equals(o.A) && B.Equals(o.B) && C.Equals(o.C); }
        public override bool Equals(object o) { return o is Tri && Equals((Tri)o); }
        public override int GetHashCode() { unchecked { return (A.GetHashCode()*397^B.GetHashCode())*397^C.GetHashCode(); } }
    }

    public sealed class Part { public string Name; public int Base; public List<Sub> Models = new List<Sub>(); }
    public sealed class Sub
    {
        public string Name;
        public V3[] Vertices, Normals;
        public byte[] VertexBones, NormalBones;
        public List<Mesh> Meshes = new List<Mesh>();
        internal int Header, VertexData, NormalData;
    }
    public sealed class Mesh
    {
        public int SkinRef;
        public List<Tri> Faces = new List<Tri>();
        internal int NormBase, NormCount, Header;
        internal List<Primitive> Commands = new List<Primitive>();
    }
    internal sealed class Primitive { internal bool Fan; internal Corner[] Corners; }
    public sealed class Texture { public string Name; public int Flags, Width, Height; public byte[] Pixels, Palette; }
    public sealed class Bone { public string Name; public int Parent; public float[] Value, Scale; }
    public sealed class Sequence
    {
        public string Name;
        public int Frames, Blends, Group, AnimOffset;
        public float Fps;
        internal int Motion, MotionBone;
    }

    // Studio MDL v10, little endian. All offsets in the main file are absolute
    // except animation channel offsets (relative to their mstudioanim_t).
    public sealed class Studio
    {
        public byte[] Data { get; private set; }
        public string SourcePath { get; private set; }
        public string Name { get; private set; }
        public readonly List<string> CompanionPaths = new List<string>();
        public List<Part> Parts = new List<Part>();
        public List<Texture> Textures = new List<Texture>();
        public List<Bone> Bones = new List<Bone>();
        public List<Sequence> Sequences = new List<Sequence>();
        public int[][] Skins;
        public int HitboxCount, AttachmentCount;
        public int TriangleCount { get { int total=0; foreach(Part p in Parts) foreach(Sub s in p.Models) foreach(Mesh m in s.Meshes) total=checked(total+m.Faces.Count); return total; } }

        int textureOffset, protectedEnd, geometryStart, geometryEnd;
        bool movable = true;
        long loadedCorners, loadedPixels, checkedAnimationRuns, loadedVectors, loadedSubs;
        static readonly Encoding Names = Encoding.GetEncoding(1252);
        Studio() {}
        static InvalidDataException Bad(string key, params object[] args)
        {
            return LocalizedText.InvalidData("studio.invalidModel", new LocalizedText(key, args));
        }
        void Range(int pos, long length, string sectionKey)
        {
            if(pos<0 || length<0 || (long)pos+length>Data.Length)
                throw Bad("studio.sectionBounds", new LocalizedText(sectionKey));
        }
        void Table(int pos,int count,int stride,string sectionKey,bool protect)
        {
            if(count<0)throw Bad("studio.sectionCount", new LocalizedText(sectionKey));
            if(count==0)return;
            int size=checked(count*stride);
            Range(pos,size,sectionKey);
            if(protect)Protect(pos,size);
        }
        readonly List<KeyValuePair<int,int>> metadataRanges = new List<KeyValuePair<int,int>>();
        void Protect(int pos,int size) {
            protectedEnd=Math.Max(protectedEnd,pos+size);
            metadataRanges.Add(new KeyValuePair<int,int>(pos,pos+size));
        }
        void Geometry(int pos,int size)
        {
            if(size==0)return;
            Range(pos,size,"section.geometry");
            geometryStart=Math.Min(geometryStart,pos); geometryEnd=Math.Max(geometryEnd,pos+size);
            geometryRanges.Add(new KeyValuePair<int,int>(pos,pos+size));
        }
        readonly List<KeyValuePair<int,int>> geometryRanges = new List<KeyValuePair<int,int>>();
        int I(int p) { return BitConverter.ToInt32(Data,p); }
        short H(int p) { return BitConverter.ToInt16(Data,p); }
        int U16(int p) { return Data[p] | Data[p+1]<<8; }
        float F(int p) { float f=BitConverter.ToSingle(Data,p); if(float.IsInfinity(f)||float.IsNaN(f)) throw Bad("studio.coordinate"); return f; }
        V3 V(int p) {return new V3(F(p),F(p+4),F(p+8));}
        string S(int p,int length) { int n=0; while(n<length&&Data[p+n]!=0)n++; return Names.GetString(Data,p,n); }
        byte[] Slice(int p,int n) {byte[] dst=new byte[n]; Buffer.BlockCopy(Data,p,dst,0,n);return dst;}
        public static Studio Load(string path)
        {
            string full=DiskPath.Full(path);
            long bytes=new FileInfo(full).Length;
            if(bytes<244 || bytes>256L*1024*1024)throw Bad("studio.studioSize");
            Studio mdl=new Studio { SourcePath=full, Data=File.ReadAllBytes(full) };
            try { mdl.InlineCompanions(); mdl.Parse(); }
            catch(OverflowException ex) {
                throw new LocalizedText("studio.overflow").AsException(message => new InvalidDataException(message, ex));
            }
            return mdl;
        }
        readonly Dictionary<int,int> animationEnds=new Dictionary<int,int>();
        const int FileLimit=256*1024*1024;
        static string Leaf(string name){ return Path.GetFileName(name.Replace('\\','/')); }
        string FindCompanion(string first,string second)
        {
            string dir=Path.GetDirectoryName(SourcePath);
            foreach(string candidate in new string[]{first,second}) {
                string name=Leaf(candidate);
                if(string.IsNullOrWhiteSpace(name)||!name.EndsWith(".mdl",StringComparison.OrdinalIgnoreCase)||
                    name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)continue;
                string file=DiskPath.Full(Path.Combine(dir,name));
                if(!string.Equals(DiskPath.Display(file),DiskPath.Display(SourcePath),StringComparison.OrdinalIgnoreCase)&&File.Exists(file))return file;
            }
            throw new LocalizedText("studio.missingCompanion", first).AsException(message => new FileNotFoundException(message));
        }
        byte[] SideFile(string file,int signature,int minimum)
        {
            long len=new FileInfo(file).Length;
            if(len<minimum||len>FileLimit)throw Bad("studio.companionSize", Path.GetFileName(file));
            byte[] b=File.ReadAllBytes(file);
            if(BitConverter.ToInt32(b,0)!=signature||BitConverter.ToInt32(b,4)!=10||BitConverter.ToInt32(b,72)!=b.Length)
                throw Bad("studio.companionSignature", Path.GetFileName(file));
            CompanionPaths.Add(file);return b;
        }
        static void ExternalRange(byte[] b,int offset,long size)
        {
            if(offset<0||size<0||(long)offset+size>b.Length)throw Bad("studio.companionBounds");
        }
        // A standalone output can be renamed and installed safely. Track-relative
        // RLE channels need no re-encoding when an entire IDSQ payload is moved.
        void InlineCompanions()
        {
            if(I(0)!=0x54534449||I(4)!=10||I(72)!=Data.Length)return;
            int groups=I(172),nt=I(180);
            if((groups==1||groups==0&&I(164)==0)&&nt>0)return;
            if(groups<0||groups>256||groups==0&&I(164)!=0||nt<0)throw Bad("studio.groupsTextures");
            int groupOff=I(176),seqs=I(164),seqOff=I(168);
            Range(groupOff,(long)groups*104,"section.sequenceGroups");
            Range(seqOff,(long)seqs*176,"section.sequences");
            if(seqs<0||seqs>65535)throw Bad("studio.sequenceCount");
            string stem=Path.GetFileNameWithoutExtension(SourcePath);
            using(var merged=new MemoryStream())using(var w=new BinaryWriter(merged)) {
                w.Write(Data);
                var changes=new Dictionary<int,int>();
                int[] offsets=new int[groups],limits=new int[groups];
                for(int j=0;j<seqs;j++) {
                    int s=seqOff+176*j,group=I(s+156);
                    if(group<0||group>=groups)throw Bad("studio.sequenceGroupIndex");
                    if(group==0)continue;
                    if(limits[group]==0) {
                        string entry=S(groupOff+104*group+32,64);
                        string generated=stem+group.ToString("D2",System.Globalization.CultureInfo.InvariantCulture)+".mdl";
                        string path=FindCompanion(entry.Length==0?generated:entry,generated);
                        byte[] chunk=SideFile(path,0x51534449,76);
                        Align(w);
                        if(merged.Position+chunk.Length-76>FileLimit)throw Bad("studio.familySize");
                        offsets[group]=(int)merged.Position-76;
                        w.Write(chunk,76,chunk.Length-76);
                        limits[group]=(int)merged.Position;
                    }
                    if(I(s+124)<76)throw Bad("studio.idsqOffset");
                    changes[s+124]=checked(I(s+124)+offsets[group]);changes[s+156]=0;
                    animationEnds[j]=limits[group];
                }
                if(groups>1)changes[172]=1;
                if(nt==0) {
                    // StudioMDL stores the skin-family table with the external textures.
                    string internalStem=Path.GetFileNameWithoutExtension(Leaf(S(8,64)));
                    string texturePath=FindCompanion(stem+"T.mdl",internalStem+"T.mdl");
                    byte[] t=SideFile(texturePath,0x54534449,244);
                    int n=BitConverter.ToInt32(t,180),table=BitConverter.ToInt32(t,184);
                    int refs=BitConverter.ToInt32(t,192),sk=BitConverter.ToInt32(t,196),skOff=BitConverter.ToInt32(t,200);
                    if(n<1||n>4096||refs<1||refs>4096||sk<1||sk>4096)throw Bad("studio.externalTextureTables");
                    ExternalRange(t,table,n*80);ExternalRange(t,skOff,2L*refs*sk);
                    if(merged.Position+(long)n*80+2L*refs*sk>FileLimit)throw Bad("studio.familySize");
                    Align(w);int newTable=(int)merged.Position; w.Write(t,table,80*n);
                    Align(w);int newSk=(int)merged.Position;w.Write(t,skOff,2*refs*sk);
                    Align(w);changes[188]=(int)merged.Position;
                    for(int j=0;j<n;j++) {
                        int record=table+j*80,wide=BitConverter.ToInt32(t,record+68),high=BitConverter.ToInt32(t,record+72);
                        if(wide<1||high<1)throw Bad("studio.externalTextureSize");
                        int length=checked(wide*high+768),pixels=BitConverter.ToInt32(t,record+76);
                        ExternalRange(t,pixels,length);Align(w);
                        if(merged.Position+length>FileLimit)throw Bad("studio.familySize");
                        changes[newTable+j*80+76]=(int)merged.Position;w.Write(t,pixels,length);
                    }
                    changes[180]=n;changes[184]=newTable;changes[192]=refs;changes[196]=sk;changes[200]=newSk;
                }
                Align(w);Data=merged.ToArray();
                foreach(var c in changes)Set(Data,c.Key,c.Value);
                Set(Data,72,Data.Length);
            }
        }
        void Parse()
        {
            Range(0,244,"section.header");
            if(I(0)!=0x54534449 || I(4)!=10)throw Bad("studio.headerFormat");
            if(I(72)!=Data.Length)throw Bad("studio.fileLength");
            Name=S(8,64); geometryStart=Data.Length; protectedEnd=244;
            int boneCount=I(140), bo=I(144);
            if(boneCount<1 || boneCount>256)throw Bad("studio.boneCount");
            Table(bo,boneCount,112,"section.bones",true);
            for(int j=0;j<boneCount;j++) {
                int q=bo+j*112;
                Bone b=new Bone { Name=S(q,32), Parent=I(q+32), Value=new float[6], Scale=new float[6] };
                if(b.Parent < -1 || b.Parent>=j)throw Bad("studio.boneHierarchy");
                for(int k=0;k<6;k++) { b.Value[k]=F(q+64+k*4); b.Scale[k]=F(q+88+k*4); }
                Bones.Add(b);
            }
            Table(I(152),I(148),24,"section.controllers",true);
            HitboxCount=I(156); AttachmentCount=I(212);
            Table(I(160),HitboxCount,32,"section.hitboxes",true);
            Table(I(216),AttachmentCount,88,"section.attachments",true);
            for(int j=0;j<HitboxCount;j++) CheckBone(I(I(160)+j*32));
            for(int j=0;j<AttachmentCount;j++) CheckBone(I(I(216)+j*88+36));
            int transitions=I(236);
            if(transitions<0 || transitions>255)throw Bad("studio.transitionTable");
            Table(I(240),transitions,transitions,"section.transitions",true);
            for(int i=220;i<=232;i+=4) if(I(i)!=0)movable=false;
            LoadSequences();
            LoadTextures();
            int parts=I(204),bp=I(208);
            Table(bp,parts,76,"section.parts",true);
            if(parts<1 || parts>4096)throw Bad("studio.partCount");
            for(int j=0;j<parts;j++) {
                int p=bp+76*j, count=I(p+64), off=I(p+72);
                Part part=new Part { Name=S(p,64), Base=I(p+68) };
                loadedSubs+=count;
                if(part.Base<1 || count<1 || count>4096 || loadedSubs>32768 || loadedSubs*112>2L*Data.Length)
                    throw Bad("studio.bodygroupOptions");
                Table(off,count,112,"section.submodels",false);
                protectedEnd=Math.Max(protectedEnd,off+count*112);
                for(int k=0;k<count;k++)part.Models.Add(LoadSub(off+k*112));
                Parts.Add(part);
            }
            Protect(0,8); Protect(76,108); Protect(192,8); Protect(204,40);
            MergeRanges(metadataRanges);
        }
        void CheckBone(int id) { if(id<0 || id>=Bones.Count)throw Bad("studio.boneIndex"); }
        void LoadTextures()
        {
            int count=I(180); textureOffset=I(184);
            if(count==0)
                throw new LocalizedText("studio.externalTextures").AsException(message => new NotSupportedException(message));
            if(count<0||count>4096)throw Bad("studio.textureCount");
            Table(textureOffset,count,80,"section.textures",false);
            for(int j=0;j<count;j++) {
                int p=textureOffset+80*j;
                Texture t=new Texture { Name=S(p,64), Flags=I(p+64), Width=I(p+68), Height=I(p+72) };
                if(t.Width<1 || t.Height<1)throw Bad("studio.textureSize");
                int size=checked(t.Width*t.Height), pix=I(p+76);
                Range(pix,checked(size+768),"section.pixelsPalette");
                loadedPixels+=size+768;
                if(loadedPixels>2L*Data.Length)throw Bad("studio.textureOverlap");
                if(pix<textureOffset)movable=false;
                t.Pixels=Slice(pix,size); t.Palette=Slice(pix+size,768);
                Textures.Add(t);
            }
            int refs=I(192), families=I(196), so=I(200);
            if(refs<1 || refs>4096 || families<1 || families>4096)throw Bad("studio.skinFamilies");
            Table(so,checked(refs*families),2,"section.skins",false);
            if(so<textureOffset || I(188)<textureOffset)movable=false;
            Skins=new int[families][];
            for(int s=0;s<families;s++) {
                Skins[s]=new int[refs];
                for(int j=0;j<refs;j++) {
                    int t=H(so+2*(s*refs+j));
                    if(t<0||t>=count)throw Bad("studio.textureIndex");
                    Skins[s][j]=t;
                }
            }
        }
        void LoadSequences()
        {
            int groups=I(172),go=I(176),count=I(164),seq=I(168);
            if(groups==0&&count==0)return;
            if(groups!=1)throw Bad("studio.sequenceGroups");
            Table(go,groups,104,"section.sequenceGroups",true);
            Table(seq,count,176,"section.sequences",true);
            if(count<0||count>65535)throw Bad("studio.sequenceCount");
            var covered=new Dictionary<int,int>();
            for(int j=0;j<count;j++) {
                int p=seq+176*j;
                Sequence s=new Sequence { Name=S(p,32), Fps=F(p+32), Frames=I(p+56), Blends=I(p+120),
                    Group=I(p+156), Motion=I(p+68), MotionBone=I(p+72) };
                if(s.Group!=0)throw Bad("studio.sequenceGroup");
                if(s.Frames<1||s.Frames>1000000||s.Blends<1||s.Blends>256)throw Bad("studio.animationSize");
                if((s.Motion&7)!=0)CheckBone(s.MotionBone);
                Table(I(p+52),I(p+48),76,"section.events",true);
                Table(I(p+64),I(p+60),20,"section.pivots",true);
                if(I(p+88)!=0 || I(p+92)!=0)movable=false;
                s.AnimOffset=I(p+124);
                int tracks=checked(Bones.Count*s.Blends);
                Table(s.AnimOffset,tracks,12,"section.animationChannels",true);
                int limit;
                if(!animationEnds.TryGetValue(j,out limit))limit=Data.Length;
                if((long)s.AnimOffset+tracks*12>limit)throw Bad("studio.channelsOutsideGroup");
                for(int k=0;k<tracks;k++) {
                    int anim=s.AnimOffset+12*k;
                    for(int axis=0;axis<6;axis++) {
                        int offset=U16(anim+2*axis);
                        if(offset==0)continue;
                        int start=checked(anim+offset), old;
                        if(start>=limit)throw Bad("studio.channelOutsideGroup");
                        if(covered.TryGetValue(start,out old)&&old>=s.Frames)continue;
                        int q=start,frames=0;
                        while(frames<s.Frames) {
                            Range(q,2,"section.rleStart");
                            int valid=Data[q], total=Data[q+1], bytes=2+2*valid;
                            if(valid<1 || valid>total)throw Bad("studio.rleRun");
                            Range(q,bytes,"section.rleValues");
                            if(++checkedAnimationRuns>8L*Data.Length)throw Bad("studio.channelOverlap");
                            frames+=total; q+=bytes;
                        }
                        if(q>limit)throw Bad("studio.rleChannelOutsideGroup");
                        covered[start]=frames; Protect(start,q-start);
                    }
                }
                Sequences.Add(s);
            }
        }
        Sub LoadSub(int p)
        {
            Protect(p,72); Protect(p+104,8);
            int nmesh=I(p+72),mso=I(p+76),nv=I(p+80),vb=I(p+84),vp=I(p+88);
            int nn=I(p+92),nb=I(p+96),np=I(p+100);
            loadedVectors+=(long)nv+nn;
            if(nv<0||nv>32768||nn<0||nn>32768||nmesh<0||nmesh>4096||loadedVectors*12>2L*Data.Length)
                throw Bad("studio.submodelSize");
            if(I(p+104)!=0)
                throw new LocalizedText("studio.deformationGroups").AsException(message => new NotSupportedException(message));
            if(I(p+108)!=0)movable=false;
            Geometry(mso,nmesh*20); Geometry(vb,nv); Geometry(vp,nv*12); Geometry(nb,nn); Geometry(np,nn*12);
            Sub sub=new Sub { Name=S(p,64),Header=p,VertexData=vp,NormalData=np,
                Vertices=new V3[nv],Normals=new V3[nn],VertexBones=nv==0?new byte[0]:Slice(vb,nv),NormalBones=nn==0?new byte[0]:Slice(nb,nn) };
            for(int i=0;i<nv;i++){CheckBone(sub.VertexBones[i]);sub.Vertices[i]=V(vp+i*12);}
            for(int i=0;i<nn;i++){CheckBone(sub.NormalBones[i]);sub.Normals[i]=V(np+i*12);}
            int normalBase=0;
            for(int i=0;i<nmesh;i++) {
                int q=mso+20*i, expected=I(q), ofs=I(q+4);
                Mesh m=new Mesh { Header=q, SkinRef=I(q+8), NormCount=I(q+12), NormBase=normalBase };
                if(m.SkinRef<0||m.SkinRef>=Skins[0].Length||m.NormCount<0||m.NormCount>nn-normalBase)throw Bad("studio.meshMaterialNormals");
                if(I(q+16)!=0)
                    throw new LocalizedText("studio.normalIndexLayout").AsException(message => new NotSupportedException(message));
                if(expected<0||expected>Data.Length/8)throw Bad("studio.triangleCount");
                int pos=ofs;
                while(true) {
                    Range(pos,2,"section.meshCommand");
                    int cmd=H(pos);pos+=2;
                    if(cmd==0)break;
                    int len=Math.Abs(cmd);
                    if(len<3 || m.Faces.Count+(long)len-2>expected)throw Bad("studio.meshStripFan");
                    Range(pos,len*8,"section.stripFanVertices");
                    loadedCorners+=len;
                    if(loadedCorners>Data.Length/2L || loadedCorners>12000000)throw Bad("studio.meshOverlap");
                    Corner[] items=new Corner[len];
                    for(int k=0;k<len;k++,pos+=8) {
                        var c=new Corner(H(pos),H(pos+2),H(pos+4),H(pos+6));
                        if(c.V<0||c.V>=nv||c.N<normalBase||c.N>=normalBase+m.NormCount)throw Bad("studio.vertexNormalIndex");
                        items[k]=c;
                        if(k>=2) {
                            Corner a=items[cmd<0?0:k-2],b=items[k-1];
                            m.Faces.Add(cmd>0&&(k&1)!=0?new Tri(b,a,c):new Tri(a,b,c));
                        }
                    }
                    m.Commands.Add(new Primitive { Fan=cmd<0, Corners=items });
                }
                if(m.Faces.Count!=expected)throw Bad("studio.commandTriangles");
                Geometry(ofs,pos-ofs);
                sub.Meshes.Add(m); normalBase+=m.NormCount;
            }
            if(normalBase!=nn)throw Bad("studio.submodelNormals");
            return sub;
        }
        public XForm[] BindPose() { return TransformPose(null,0,0); }
        public XForm[] Pose(int sequence,int frame,int blend)
        {
            if(sequence<0||sequence>=Sequences.Count)throw new ArgumentOutOfRangeException("sequence");
            Sequence s=Sequences[sequence];
            if(frame<0||frame>=s.Frames||blend<0||blend>=s.Blends)throw new ArgumentOutOfRangeException("frame/blend");
            return TransformPose(s,frame,blend);
        }
        XForm[] TransformPose(Sequence s,int frame,int blend)
        {
            var transforms=new XForm[Bones.Count];
            float[] v=new float[6];
            for(int j=0;j<Bones.Count;j++) {
                Bone b=Bones[j];
                for(int k=0;k<6;k++) {
                    v[k]=b.Value[k];
                    if(s==null)continue;
                    int track=s.AnimOffset+(blend*Bones.Count+j)*12,offset=U16(track+k*2);
                    if(offset!=0) {
                        int pos=track+offset, fr=frame;
                        while(fr>=Data[pos+1]) {fr-=Data[pos+1];pos+=2+2*Data[pos];}
                        v[k]+=H(pos+2*Math.Min(fr+1,(int)Data[pos]))*b.Scale[k];
                    }
                    if(j==s.MotionBone && k<3 && (s.Motion&(1<<k))!=0)v[k]=0;
                }
                XForm local=XForm.Local(v[0],v[1],v[2],v[3],v[4],v[5]);
                transforms[j]=b.Parent==-1?local:transforms[b.Parent].Then(local);
            }
            return transforms;
        }

        static void MergeRanges(List<KeyValuePair<int,int>> spans)
        {
            spans.Sort(delegate(KeyValuePair<int,int> a,KeyValuePair<int,int> b){return a.Key.CompareTo(b.Key);});
            int write=0;
            for(int i=0;i<spans.Count;i++) {
                var s=spans[i];
                if(write>0 && spans[write-1].Value>=s.Key) {
                    var last=spans[write-1];
                    spans[write-1]=new KeyValuePair<int,int>(last.Key,Math.Max(last.Value,s.Value));
                } else spans[write++]=s;
            }
            spans.RemoveRange(write,spans.Count-write);
        }
        static bool SameBytes(byte[] a,int offA,byte[] b,int offB,int len)
        {
            if((long)offB+len>b.Length || offB<0)return false;
            for(int j=0;j<len;j++)if(a[offA+j]!=b[offB+j])return false;
            return true;
        }
        public void VerifyMetadata(Studio other)
        {
            // Check original binary descriptors, events and all referenced animation
            // samples. Model geometry offsets, output length and name may legitimately change.
            foreach(var span in metadataRanges)if(!SameBytes(Data,span.Key,other.Data,span.Key,span.Value-span.Key))
                throw Bad("studio.metadataChanged");
            if(Textures.Count!=other.Textures.Count || Parts.Count!=other.Parts.Count || Skins.Length!=other.Skins.Length)
                throw Bad("studio.structureChanged");
            for(int i=0;i<Textures.Count;i++) {
                Texture a=Textures[i],b=other.Textures[i];
                if(!SameBytes(Data,textureOffset+80*i,other.Data,other.textureOffset+80*i,76)
                    ||a.Pixels.Length!=b.Pixels.Length||!SameBytes(a.Pixels,0,b.Pixels,0,a.Pixels.Length)
                    ||!SameBytes(a.Palette,0,b.Palette,0,768))throw Bad("studio.textureChanged");
            }
            for(int i=0;i<Skins.Length;i++) {
                if(Skins[i].Length!=other.Skins[i].Length)throw Bad("studio.skinFamilyChanged");
                for(int j=0;j<Skins[i].Length;j++)if(Skins[i][j]!=other.Skins[i][j])throw Bad("studio.skinSelectionChanged");
            }
            for(int i=0;i<Parts.Count;i++) {
                if(Parts[i].Models.Count!=other.Parts[i].Models.Count)throw Bad("studio.bodygroupChanged");
                for(int j=0;j<Parts[i].Models.Count;j++) {
                    var a=Parts[i].Models[j].Meshes; var b=other.Parts[i].Models[j].Meshes;
                    if(a.Count!=b.Count)throw Bad("studio.materialCountChanged");
                    for(int k=0;k<a.Count;k++)if(a[k].SkinRef!=b[k].SkinRef)throw Bad("studio.meshMaterialChanged");
                }
            }
        }

        struct Edge : IEquatable<Edge>
        {
            public Corner A,B;
            public Edge(Corner a,Corner b){A=a; B=b;}
            public bool Equals(Edge e){return A.Equals(e.A)&&B.Equals(e.B);}
            public override bool Equals(object o){return o is Edge&&Equals((Edge)o);}
            public override int GetHashCode(){unchecked{return A.GetHashCode()*397^B.GetHashCode();}}
        }
        struct Link {public int Id; public Corner Next;}
        sealed class Neighbors { public List<Link> Links=new List<Link>(); public int Start; }
        // Emit only correctly wound, oriented GL primitives. In exact mode the
        // face ordering is untouched, including coincident faces and blending.
        static IEnumerable<Primitive> MakePrimitives(List<Tri> tris,bool ordered)
        {
            var edges=new Dictionary<Edge,Neighbors>();
            if(!ordered)for(int i=0;i<tris.Count;i++) {
                Tri t=tris[i];
                for(int j=0;j<3;j++){
                    var key=new Edge(t[j],t[(j+1)%3]); Neighbors ns;
                    if(!edges.TryGetValue(key,out ns))edges.Add(key,ns=new Neighbors());
                    ns.Links.Add(new Link {Id=i,Next=t[(j+2)%3]});
                }
            }
            bool[] done=new bool[tris.Count];
            int[] visited=new int[tris.Count],seq=new int[126],bestSeq=new int[126];
            Corner[] points=new Corner[128],bestPoints=new Corner[128];
            int stamp=0;
            for(int i=0;i<tris.Count;i++) {
                if(done[i])continue;
                int best=0; bool bestFan=false;
                for(int orient=0;orient<6;orient++) {
                    bool fan=orient>=3; int rot=orient%3,used=1;
                    points[0]=tris[i][rot];points[1]=tris[i][(rot+1)%3];points[2]=tris[i][(rot+2)%3];
                    seq[0]=i; visited[i]=++stamp;
                    while(used<seq.Length) {
                        // To append face #used, even strip faces use (last2,last1).
                        Corner a=fan?points[0]:points[(used%2==0)?used:used+1];
                        Corner b=fan?points[used+1]:points[(used%2==0)?used+1:used];
                        int found=-1; Corner last=default(Corner);
                        if(ordered) {
                            int n=i+used;
                            if(n>=tris.Count)break;
                            Tri t=tris[n];
                            for(int j=0;j<3;j++)if(t[j].Equals(a)&&t[(j+1)%3].Equals(b)){found=n;last=t[(j+2)%3];break;}
                        } else {
                            Neighbors ns;
                            if(edges.TryGetValue(new Edge(a,b),out ns)) {
                                while(ns.Start<ns.Links.Count && done[ns.Links[ns.Start].Id])ns.Start++;
                                for(int j=ns.Start;j<ns.Links.Count;j++) {
                                    Link step=ns.Links[j];
                                    if(!done[step.Id] && visited[step.Id]!=stamp){found=step.Id;last=step.Next;break;}
                                }
                            }
                        }
                        if(found<0)break;
                        visited[found]=stamp;seq[used]=found;points[used+2]=last;used++;
                    }
                    if(used>best) {
                        best=used;bestFan=fan;
                        Array.Copy(seq,bestSeq,used);Array.Copy(points,bestPoints,used+2);
                    }
                }
                for(int j=0;j<best;j++)done[bestSeq[j]]=true;
                var strip=new Corner[best+2];Array.Copy(bestPoints,strip,best+2);
                yield return new Primitive {Fan=bestFan,Corners=strip};
            }
        }
        static bool SameFaces(List<Tri> a,List<Tri> b)
        {
            if(object.ReferenceEquals(a,b))return true;
            if(a.Count!=b.Count)return false;
            for(int j=0;j<a.Count;j++)if(!a[j].Equals(b[j]))return false;
            return true;
        }
        static void Align(BinaryWriter w){while((w.BaseStream.Position&3)!=0)w.Write((byte)0);}
        static void Set(byte[] data,int pos,int value){Buffer.BlockCopy(BitConverter.GetBytes(value),0,data,pos,4);}
        static int[] Compact(bool[] used,out int count) {
            int[] ids=new int[used.Length];count=0;
            for(int i=0;i<ids.Length;i++)ids[i]=used[i]?count++:-1;
            return ids;
        }
        int WriteBoneMap(BinaryWriter w,byte[] bones,bool[] used) {
            Align(w);int start=checked((int)w.BaseStream.Position);
            for(int j=0;j<used.Length;j++)if(used[j])w.Write(bones[j]);return start;
        }
        int WriteVectors(BinaryWriter w,int input,bool[] used) {
            Align(w);int start=checked((int)w.BaseStream.Position);
            for(int j=0;j<used.Length;j++)if(used[j])w.Write(Data,input+12*j,12);
            return start;
        }
        // Only discard the original geometry span when it consists solely of
        // verified geometry records + alignment, followed by the texture section.
        // Other layouts keep every unrecognized byte and append a new mesh block.
        bool IsolatedGeometry()
        {
            if(!movable || geometryRanges.Count==0 || geometryEnd>textureOffset)return false;
            var ranges=new List<KeyValuePair<int,int>>(geometryRanges);
            ranges.Sort(delegate(KeyValuePair<int,int> a,KeyValuePair<int,int>b){return a.Key.CompareTo(b.Key);});
            int pos=geometryStart;
            foreach(var s in ranges) {
                if(s.Key<pos||s.Key-pos>3)return false;
                pos=s.Value;
            }
            foreach(var span in metadataRanges)
                if(span.Key<geometryEnd && span.Value>geometryStart)return false;
            return true;
        }
        public void Save(string dest,Dictionary<Mesh,List<Tri>> faces) {Save(dest,faces,true,null);}
        public void Save(string dest,Dictionary<Mesh,List<Tri>> faces,bool preserveOrder){Save(dest,faces,preserveOrder,null);}
        public void Save(string dest,Dictionary<Mesh,List<Tri>> faces,bool preserveOrder,string internalName)
        {
            string full=DiskPath.Full(dest);
            if(string.Equals(DiskPath.Display(full),DiskPath.Display(SourcePath),StringComparison.OrdinalIgnoreCase)||File.Exists(full))
                throw new LocalizedText("studio.destinationExists", DiskPath.Display(full)).AsException(message => new IOException(message));
            var known=new HashSet<Mesh>();
            bool changed=false;
            foreach(Part p in Parts)foreach(Sub s in p.Models)foreach(Mesh m in s.Meshes) {
                known.Add(m); List<Tri> t;
                if(faces.TryGetValue(m,out t)) {
                    if(t==null)throw new LocalizedText("studio.nullFaceList").AsException(message => new ArgumentException(message));
                    if(!SameFaces(m.Faces,t))changed=true;
                }
            }
            foreach(var m in faces.Keys)if(!known.Contains(m))
                throw new LocalizedText("studio.foreignMesh").AsException(message => new ArgumentException(message));
            byte[] output=changed ? Rebuild(faces,preserveOrder) : (byte[])Data.Clone();
            if(internalName!=null) {
                if(internalName.IndexOf('\0')>=0||Names.GetByteCount(internalName)>63)
                    throw new LocalizedText("studio.internalName").AsException(message => new ArgumentException(message));
                Array.Clear(output,8,64);Names.GetBytes(internalName,0,internalName.Length,output,8);
            }
            string temp=DiskPath.Full(Path.Combine(Path.GetDirectoryName(full),".m-"+Guid.NewGuid().ToString("N")+".tmp"));
            bool ownTemp=false;
            try {
                using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    ownTemp=true;
                    f.Write(output,0,output.Length);
                }
                var saved=Load(temp);
                VerifyMetadata(saved);
                VerifyGeometry(saved,faces,preserveOrder);
                File.Move(temp,full);
            } finally {if(ownTemp&&File.Exists(temp))File.Delete(temp);}
        }
        byte[] Rebuild(Dictionary<Mesh,List<Tri>> faces,bool ordered)
        {
            bool isolated=IsolatedGeometry();
            bool relocate=isolated && protectedEnd<=geometryStart && textureOffset-geometryEnd<=3;
            using(var buffer=new MemoryStream())using(var w=new BinaryWriter(buffer)) {
                w.Write(Data,0,relocate?geometryStart:Data.Length);
                var edits=new Dictionary<int,int>();
                foreach(Part p in Parts)foreach(Sub s in p.Models) {
                    if(s.Meshes.Count==0 && s.Vertices.Length==0 && s.Normals.Length==0)continue;
                    bool[] vs=new bool[s.Vertices.Length],ns=new bool[s.Normals.Length];
                    var selected=new List<List<Tri>>();
                    foreach(Mesh m in s.Meshes) {
                        List<Tri> tris;if(!faces.TryGetValue(m,out tris))tris=m.Faces;
                        selected.Add(tris);
                        foreach(Tri t in tris)for(int k=0;k<3;k++) {
                            var c=t[k];
                            if(c.V<0||c.V>=vs.Length||c.N<m.NormBase||c.N>=m.NormBase+m.NormCount||
                                c.U<short.MinValue||c.U>short.MaxValue||c.T<short.MinValue||c.T>short.MaxValue)throw Bad("studio.cornerBounds");
                            vs[c.V]=true;ns[c.N]=true;
                        }
                    }
                    int nv,nn; var vm=Compact(vs,out nv);var nm=Compact(ns,out nn);
                    int mo=s.Header;
                    edits[mo+80]=nv;edits[mo+92]=nn;
                    edits[mo+84]=WriteBoneMap(w,s.VertexBones,vs);edits[mo+96]=WriteBoneMap(w,s.NormalBones,ns);
                    edits[mo+88]=WriteVectors(w,s.VertexData,vs);edits[mo+100]=WriteVectors(w,s.NormalData,ns);
                    Align(w);int table=checked((int)buffer.Position); edits[mo+76]=table;
                    w.Write(new byte[checked(20*s.Meshes.Count)]);
                    for(int j=0;j<s.Meshes.Count;j++) {
                        Mesh m=s.Meshes[j];var tris=selected[j];int n=0,q=table+20*j;
                        for(int k=m.NormBase;k<m.NormBase+m.NormCount;k++)if(ns[k])n++;
                        Align(w);edits[q]=tris.Count;edits[q+4]=checked((int)buffer.Position);edits[q+8]=m.SkinRef;edits[q+12]=n;
                        IEnumerable<Primitive> commands=SameFaces(tris,m.Faces)?(IEnumerable<Primitive>)m.Commands:MakePrimitives(tris,ordered);
                        foreach(Primitive cmd in commands) {
                            w.Write(checked((short)(cmd.Fan?-cmd.Corners.Length:cmd.Corners.Length)));
                            foreach(Corner c in cmd.Corners) {w.Write((short)vm[c.V]);w.Write((short)nm[c.N]);w.Write((short)c.U);w.Write((short)c.T);}
                        }
                        w.Write((short)0);
                    }
                }
                Align(w);
                if(relocate) {
                    int start=checked((int)buffer.Position),delta=start-textureOffset;
                    w.Write(Data,textureOffset,Data.Length-textureOffset);
                    foreach(int k in new int[]{184,188,200})edits[k]=checked(I(k)+delta);
                    for(int i=0;i<Textures.Count;i++)edits[start+i*80+76]=checked(I(textureOffset+i*80+76)+delta);
                }
                byte[] output=buffer.ToArray();
                foreach(var edit in edits)Set(output,edit.Key,edit.Value);
                // For split families the newly inlined animation streams may be
                // after the original mesh. Reuse its old span without moving those
                // streams, instead of duplicating geometry at the end of the file.
                int appendedStart=(Data.Length+3)&~3;
                if(!relocate && isolated && output.Length-appendedStart <= geometryEnd-geometryStart) {
                    int delta=geometryStart-appendedStart;
                    foreach(var p in Parts)foreach(var s in p.Models) {
                        if(s.Meshes.Count==0 && s.Vertices.Length==0 && s.Normals.Length==0)continue;
                        int table=BitConverter.ToInt32(output,s.Header+76);
                        foreach(int field in new int[]{76,84,88,96,100}) {
                            int at=s.Header+field;
                            Set(output,at,BitConverter.ToInt32(output,at)+delta);
                        }
                        for(int j=0;j<s.Meshes.Count;j++) {
                            int at=table+j*20+4;
                            Set(output,at,BitConverter.ToInt32(output,at)+delta);
                        }
                    }
                    Array.Clear(output,geometryStart,geometryEnd-geometryStart);
                    Buffer.BlockCopy(output,appendedStart,output,geometryStart,output.Length-appendedStart);
                    Array.Resize(ref output,Data.Length);
                }
                Set(output,72,output.Length);return output;
            }
        }
        struct FaceKey : IEquatable<FaceKey>
        {
            public Corner A,B,C;
            static int Cmp(Corner a,Corner b) {
                if(a.V!=b.V)return a.V.CompareTo(b.V);if(a.N!=b.N)return a.N.CompareTo(b.N);
                if(a.U!=b.U)return a.U.CompareTo(b.U);return a.T.CompareTo(b.T);
            }
            static bool Less(Corner a,Corner b,Corner c,Corner x,Corner y,Corner z) {
                int i=Cmp(a,x);if(i!=0)return i<0; i=Cmp(b,y);return i<0||(i==0&&Cmp(c,z)<0);
            }
            internal FaceKey(Corner a,Corner b,Corner c) {
                A=a;B=b;C=c;
                if(Less(b,c,a,A,B,C)){A=b;B=c;C=a;}
                if(Less(c,a,b,A,B,C)){A=c;B=a;C=b;}
            }
            public bool Equals(FaceKey f){return A.Equals(f.A)&&B.Equals(f.B)&&C.Equals(f.C);}
            public override bool Equals(object o){return o is FaceKey&&Equals((FaceKey)o);}
            public override int GetHashCode(){unchecked{return (A.GetHashCode()*397^B.GetHashCode())*397^C.GetHashCode();}}
        }
        void VerifyGeometry(Studio saved,Dictionary<Mesh,List<Tri>> plan,bool ordered)
        {
            for(int i=0;i<Parts.Count;i++)for(int j=0;j<Parts[i].Models.Count;j++) {
                var src=Parts[i].Models[j];var dst=saved.Parts[i].Models[j];
                bool[] vs=new bool[src.Vertices.Length],ns=new bool[src.Normals.Length];
                foreach(var m in src.Meshes) {
                    List<Tri> faces;if(!plan.TryGetValue(m,out faces))faces=m.Faces;
                    foreach(var t in faces)for(int k=0;k<3;k++){vs[t[k].V]=true;ns[t[k].N]=true;}
                }
                int nv,nn;var vm=Compact(vs,out nv);var nm=Compact(ns,out nn);
                // A byte-for-byte passthrough intentionally retains unused original slots.
                if(dst.Vertices.Length==src.Vertices.Length)for(int k=0;k<vm.Length;k++)vm[k]=k;
                else if(dst.Vertices.Length!=nv)throw Bad("studio.vertexCompaction");
                if(dst.Normals.Length==src.Normals.Length)for(int k=0;k<nm.Length;k++)nm[k]=k;
                else if(dst.Normals.Length!=nn)throw Bad("studio.normalCompaction");
                for(int k=0;k<vs.Length;k++)if(vs[k]&&(src.VertexBones[k]!=dst.VertexBones[vm[k]]||
                    !SameBytes(Data,src.VertexData+k*12,saved.Data,dst.VertexData+vm[k]*12,12)))throw Bad("studio.vertexBoneChanged");
                for(int k=0;k<ns.Length;k++)if(ns[k]&&(src.NormalBones[k]!=dst.NormalBones[nm[k]]||
                    !SameBytes(Data,src.NormalData+k*12,saved.Data,dst.NormalData+nm[k]*12,12)))throw Bad("studio.normalBoneChanged");
                for(int q=0;q<src.Meshes.Count;q++) {
                    var m=src.Meshes[q];List<Tri> faces;if(!plan.TryGetValue(m,out faces))faces=m.Faces;
                    var result=dst.Meshes[q].Faces;
                    if(result.Count!=faces.Count)throw Bad("studio.exportedCount");
                    var expected=new Dictionary<FaceKey,int>();
                    for(int n=0;n<faces.Count;n++) {
                        Tri t=faces[n];
                        var key=new FaceKey(Remap(t.A,vm,nm),Remap(t.B,vm,nm),Remap(t.C,vm,nm));
                        Tri actual=result[n];
                        if(ordered) {
                            if(!key.Equals(new FaceKey(actual.A,actual.B,actual.C)))throw Bad("studio.exportOrder");
                        } else {int count; expected.TryGetValue(key,out count);expected[key]=count+1;}
                    }
                    if(!ordered)foreach(Tri actual in result) {
                        var key=new FaceKey(actual.A,actual.B,actual.C);int count;
                        if(!expected.TryGetValue(key,out count)||count<1)throw Bad("studio.exportAttributes");
                        expected[key]=count-1;
                    }
                }
            }
        }
        static Corner Remap(Corner c,int[] v,int[] n){c.V=v[c.V];c.N=n[c.N];return c;}
    }
}
