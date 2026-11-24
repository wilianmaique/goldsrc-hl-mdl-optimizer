using System;
using System.Collections.Generic;

namespace MdlOptimizer
{
    // Orthographic diagnostic renderer. The original and candidate share the same
    // camera, scale, sample positions, light and nearest-neighbour texture filtering.
    internal sealed class ViewImage
    {
        public const int Background = 0xe1e6eb;
        internal readonly int Width, Height;
        internal readonly int[] Color, Owner;
        internal readonly float[] Depth;
        internal readonly Framing Camera;
        internal int[] Face;
        internal ViewImage(int width, int height,Framing camera=null)
        {
            Width=width; Height=height; Camera=camera;
            int n=checked(width*height);
            Color=new int[n]; Owner=new int[n]; Depth=new float[n];
            for (int i=0;i<n;i++) { Color[i]=Background; Depth[i]=float.NegativeInfinity; }
        }
    }

    internal sealed class AuditView
    {
        internal int Sequence, Frame, Blend, Skin, Resolution, Focus = -1;
        internal int[] Models;
        internal double Azimuth, Elevation;
        internal string Name;
        internal LocalizedText Category, DisplayName;
        internal bool Cache, MainBody;
        internal ViewImage Reference;
    }

    internal sealed class ScreenSub
    {
        internal Sub Sub;
        internal V3[] Points;
        internal float[] Light, ChromeU, ChromeV;
    }

    internal sealed class Framing
    {
        internal int W,H;
        internal float Scale,X,Y;
    }

    internal sealed class Box2
    {
        internal float X0=float.PositiveInfinity, Y0=float.PositiveInfinity;
        internal float X1=float.NegativeInfinity, Y1=float.NegativeInfinity;
        internal bool Empty { get { return X1<X0; } }
        internal void Add(V3 p) {
            if(p.X<X0)X0=p.X; if(p.X>X1)X1=p.X;
            if(p.Y<Y0)Y0=p.Y; if(p.Y>Y1)Y1=p.Y;
        }
    }

    internal sealed class AuditGeometry
    {
        internal readonly Studio Studio;
        internal readonly List<Mesh> Meshes=new List<Mesh>();
        internal readonly Dictionary<Mesh,int> Id=new Dictionary<Mesh,int>();
        internal readonly int[][] SkinByOwner;
        internal readonly int[][] Palettes;
        internal AuditGeometry(Studio s)
        {
            Studio=s;
            foreach(Part part in s.Parts) foreach(Sub sub in part.Models) foreach(Mesh mesh in sub.Meshes)
            {
                Meshes.Add(mesh); Id.Add(mesh,Meshes.Count); // zero means background
            }
            SkinByOwner=new int[s.Skins.Length][];
            for(int f=0;f<s.Skins.Length;f++) {
                int[] ids=new int[Meshes.Count+1]; ids[0]=-1;
                for(int j=0;j<Meshes.Count;j++) ids[j+1]=s.Skins[f][Meshes[j].SkinRef];
                SkinByOwner[f]=ids;
            }
            Palettes=new int[s.Textures.Count][];
            for(int j=0;j<s.Textures.Count;j++) {
                byte[] p=s.Textures[j].Palette;
                int[] colors=new int[256];
                for(int i=0;i<256;i++) colors[i]=(p[3*i]<<16)|(p[3*i+1]<<8)|p[3*i+2];
                Palettes[j]=colors;
            }
        }

        static float Dot(V3 a,V3 b) { return a.X*b.X+a.Y*b.Y+a.Z*b.Z; }

        internal List<ScreenSub> Project(AuditView view, bool[] focusBones, out Box2 full, out Box2 region,bool measureBounds=true)
        {
            double a=view.Azimuth*Math.PI/180.0, e=view.Elevation*Math.PI/180.0;
            float ca=(float)Math.Cos(a),sa=(float)Math.Sin(a),ce=(float)Math.Cos(e),se=(float)Math.Sin(e);
            V3 front=new V3(ca*ce,sa*ce,se), right=new V3(-sa,ca,0), up=new V3(-ca*se,-sa*se,ce);
            V3 light=new V3(.45f*front.X-.25f*right.X+.75f*up.X,
                            .45f*front.Y-.25f*right.Y+.75f*up.Y,
                            .45f*front.Z-.25f*right.Z+.75f*up.Z);
            XForm[] pose=view.Sequence<0 ? Studio.BindPose() : Studio.Pose(view.Sequence,view.Frame,view.Blend);
            full=new Box2(); region=new Box2();
            var result=new List<ScreenSub>();
            for(int part=0;part<Studio.Parts.Count;part++) {
                Sub sub=Studio.Parts[part].Models[view.Models[part]];
                var scr=new ScreenSub { Sub=sub, Points=new V3[sub.Vertices.Length], Light=new float[sub.Normals.Length],
                                        ChromeU=new float[sub.Normals.Length],ChromeV=new float[sub.Normals.Length] };
                result.Add(scr);
                for(int j=0;j<scr.Points.Length;j++) {
                    int bone=sub.VertexBones[j];
                    V3 p=pose[bone].Point(sub.Vertices[j]);
                    V3 q=new V3(Dot(p,right),Dot(p,up),Dot(p,front));
                    scr.Points[j]=q;
                    if(measureBounds) {
                        full.Add(q);
                        if(focusBones!=null && focusBones[bone]) region.Add(q);
                    }
                }
                for(int j=0;j<scr.Light.Length;j++) {
                    V3 n=pose[sub.NormalBones[j]].Vector(sub.Normals[j]);
                    scr.Light[j]=.62f+.38f*Math.Max(0,Math.Min(1,Dot(n,light)));
                    scr.ChromeU[j]=.5f+.495f*Dot(n,right);
                    scr.ChromeV[j]=.5f-.495f*Dot(n,up);
                }
            }
            return result;
        }

        internal static Framing Frame(int res,Box2 full,Box2 region,bool focus)
        {
            if(full.Empty) return new Framing {W=16,H=16,Scale=1};
            Box2 target=focus&&!region.Empty ? region : full;
            float cx=(target.X0+target.X1)*.5f,cy=(target.Y0+target.Y1)*.5f;
            float body=Math.Max(full.X1-full.X0,full.Y1-full.Y0);
            float minSpan=Math.Max(.001f,body*(focus?.10f:.06f));
            float w=Math.Max(minSpan,(target.X1-target.X0)*(focus?1.30f:1.05f));
            float h=Math.Max(minSpan,(target.Y1-target.Y0)*(focus?1.30f:1.05f));
            float scale=(res-4)/Math.Max(w,h);
            int pixelsW=Math.Max(8,Math.Min(res,(int)Math.Ceiling(w*scale)+4));
            int pixelsH=Math.Max(8,Math.Min(res,(int)Math.Ceiling(h*scale)+4));
            return new Framing { W=pixelsW,H=pixelsH,Scale=scale,X=cx-(pixelsW-1)*.5f/scale,Y=cy-(pixelsH-1)*.5f/scale };
        }

        internal static void ApplyFrame(List<ScreenSub> selected, Framing f)
        {
            foreach(ScreenSub sub in selected) for(int j=0;j<sub.Points.Length;j++) {
                V3 p=sub.Points[j];
                sub.Points[j]=new V3((p.X-f.X)*f.Scale,(p.Y-f.Y)*f.Scale,p.Z);
            }
        }

        internal ViewImage Draw(AuditView view,List<ScreenSub> selected,Framing framing,Dictionary<Mesh,List<Tri>> edit,bool trace=false)
        {
            var image=new ViewImage(framing.W,framing.H,framing);
            if(trace)image.Face=new int[image.Width*image.Height];
            for(int pass=0;pass<2;pass++) {
                foreach(ScreenSub scr in selected) foreach(Mesh mesh in scr.Sub.Meshes) {
                    int id=Id[mesh], material=SkinByOwner[view.Skin][id];
                    Texture texture=Studio.Textures[material];
                    bool additive=(texture.Flags & 32)!=0;
                    if(additive!=(pass==1)) continue;
                    List<Tri> tris;
                    if(edit==null||!edit.TryGetValue(mesh,out tris)) tris=mesh.Faces;
                    int[] palette=Palettes[material];
                    for(int fi=0;fi<tris.Count;fi++) Triangle(image,scr,tris[fi],id,texture,palette,additive,fi);
                }
            }
            return image;
        }

        static bool Earlier(V3 a,V3 b)
        {
            return a.X<b.X || (a.X==b.X && (a.Y<b.Y || (a.Y==b.Y && a.Z<b.Z)));
        }

        static int Wrap(int value,int size)
        {
            if((uint)value<(uint)size) return value;
            int r=value%size; return r<0?r+size:r;
        }

        static void Triangle(ViewImage im,ScreenSub scr,Tri triangle,int id,Texture tex,int[] palette,bool additive,int fi)
        {
            Corner a=triangle.A,b=triangle.B,c=triangle.C;
            V3 pa=scr.Points[a.V],pb=scr.Points[b.V],pc=scr.Points[c.V];
            // Strips and fans may rotate the three corners on export. Canonical rotation
            // keeps texture samples and depth ties bit-identical for the same surface.
            if(Earlier(pb,pa)&&Earlier(pb,pc)) { Corner t=a;a=b;b=c;c=t; V3 p=pa;pa=pb;pb=pc;pc=p; }
            else if(Earlier(pc,pa)) { Corner t=c;c=b;b=a;a=t; V3 p=pc;pc=pb;pb=pa;pa=p; }
            double ax=pb.X-pa.X,ay=pb.Y-pa.Y,bx=pc.X-pa.X,by=pc.Y-pa.Y, determinant=ax*by-bx*ay;
            if(determinant>=-1e-9) return; // GoldSrc studio models have clockwise fronts.
            int left=Math.Max(0,(int)Math.Ceiling(Math.Min(pa.X,Math.Min(pb.X,pc.X))));
            int right=Math.Min(im.Width-1,(int)Math.Floor(Math.Max(pa.X,Math.Max(pb.X,pc.X))));
            int bottom=Math.Max(0,(int)Math.Ceiling(Math.Min(pa.Y,Math.Min(pb.Y,pc.Y))));
            int top=Math.Min(im.Height-1,(int)Math.Floor(Math.Max(pa.Y,Math.Max(pb.Y,pc.Y))));
            if(left>right||bottom>top)return;
            double inv=1/determinant,dg=by*inv,dk=-ay*inv;
            double u=a.U,t0=a.T,du1=b.U-u,du2=c.U-u,dt1=b.T-t0,dt2=c.T-t0;
            int tw=tex.Width,th=tex.Height,flags=tex.Flags;
            if((flags&2)!=0) {
                u=scr.ChromeU[a.N]*tw;t0=scr.ChromeV[a.N]*th;
                du1=scr.ChromeU[b.N]*tw-u;du2=scr.ChromeU[c.N]*tw-u;
                dt1=scr.ChromeV[b.N]*th-t0;dt2=scr.ChromeV[c.N]*th-t0;
            }
            bool masked=(flags&64)!=0,fullBright=(flags&4)!=0,flat=(flags&1)!=0;
            double l0=flat?.88:scr.Light[a.N],dl1=flat?0:scr.Light[b.N]-l0,dl2=flat?0:scr.Light[c.N]-l0;
            double dz1=pb.Z-pa.Z,dz2=pc.Z-pa.Z;
            for(int y=bottom;y<=top;y++) {
                double dy=y-pa.Y, dx=left-pa.X, g=(dx*by-dy*bx)*inv, k=(dy*ax-dx*ay)*inv;
                int offset=(im.Height-y-1)*im.Width+left;
                for(int x=left;x<=right;x++,g+=dg,k+=dk,offset++) {
                    if(g< -1e-9||k< -1e-9||g+k>1.000000001) continue;
                    float z=(float)(pa.Z+g*dz1+k*dz2);
                    if(additive ? z<im.Depth[offset]-0.00001f : z<=im.Depth[offset]) continue;
                    int col=tex.Pixels[Wrap((int)Math.Floor(t0+g*dt1+k*dt2),th)*tw+Wrap((int)Math.Floor(u+g*du1+k*du2),tw)];
                    if(masked && col==255)continue;
                    int rgb=palette[col];
                    if(!fullBright) {
                        double light=l0+g*dl1+k*dl2;
                        rgb=((int)((rgb>>16)*light+.5)<<16)|((int)(((rgb>>8)&255)*light+.5)<<8)|(int)((rgb&255)*light+.5);
                    }
                    if(additive) {
                        if(rgb==0)continue;
                        int old=im.Color[offset];
                        rgb=(Math.Min(255,(old>>16)+(rgb>>16))<<16)
                            |(Math.Min(255,((old>>8)&255)+((rgb>>8)&255))<<8)|Math.Min(255,(old&255)+(rgb&255));
                    } else im.Depth[offset]=z;
                    im.Color[offset]=rgb; im.Owner[offset]=id;
                    if(im.Face!=null)im.Face[offset]=fi;
                }
            }
        }
    }
}
