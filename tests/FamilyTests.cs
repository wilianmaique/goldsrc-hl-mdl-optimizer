using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using MdlOptimizer;

// Builds a real split Studio texture / animation family from a bundled fixture.
// Only scratch copies are modified; no SDK, game, Internet or Steam is required.
internal static class FamilyTests
{
    static int Read(byte[] b,int p) { return BitConverter.ToInt32(b,p); }
    static void Put(byte[] b,int p,int value) { Buffer.BlockCopy(BitConverter.GetBytes(value),0,b,p,4); }
    static void Check(bool ok,string detail) { if(!ok)throw new Exception(detail); }
    static bool BytesEqual(byte[] a,byte[] b)
    {
        if(a.Length!=b.Length)return false;
        for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;
        return true;
    }
    static void BuildTextureFile(Studio original,string path)
    {
        byte[] src=original.Data;
        int table=Read(src,184), count=Read(src,180);
        // The chosen fixture uses the standard contiguous texture section.
        byte[] texture=new byte[244+src.Length-table];
        Buffer.BlockCopy(src,0,texture,0,8);
        Encoding.ASCII.GetBytes("splitT.mdl").CopyTo(texture,8);
        Buffer.BlockCopy(src,table,texture,244,src.Length-table);
        int delta=244-table;
        foreach(int offset in new int[]{180,192,196})Put(texture,offset,Read(src,offset));
        foreach(int offset in new int[]{184,188,200})Put(texture,offset,Read(src,offset)+delta);
        for(int i=0;i<count;i++)Put(texture,244+i*80+76,Read(src,table+i*80+76)+delta);
        Put(texture,72,texture.Length);
        File.WriteAllBytes(DiskPath.Full(path),texture);
    }
    static void BuildSequenceFile(Studio original,int sequence,string path)
    {
        byte[] data=original.Data;
        Sequence seq=original.Sequences[sequence];
        int start=seq.AnimOffset;
        int tracks=original.Bones.Count*seq.Blends;
        int end=start+tracks*12;
        for(int i=0;i<tracks;i++) {
            int track=start+12*i;
            for(int axis=0;axis<6;axis++) {
                int diff=BitConverter.ToUInt16(data,track+2*axis);
                if(diff==0)continue;
                int pos=track+diff,covered=0;
                while(covered<seq.Frames) {
                    covered+=data[pos+1];pos+=2+2*data[pos];
                }
                end=Math.Max(end,pos);
            }
        }
        byte[] group=new byte[76+end-start];
        Encoding.ASCII.GetBytes("IDSQ").CopyTo(group,0);
        Put(group,4,10); Put(group,72,group.Length);
        Buffer.BlockCopy(data,start,group,76,end-start);
        File.WriteAllBytes(DiskPath.Full(path),group);
    }
    static void BuildMain(Studio original,int externalSequence,string path)
    {
        byte[] data=original.Data;
        int geometryEnd=Read(data,184);
        int groups=(geometryEnd+3)&~3;
        byte[] main=new byte[groups+2*104];
        Buffer.BlockCopy(data,0,main,0,geometryEnd);
        Buffer.BlockCopy(data,Read(data,176),main,groups,104);
        Encoding.ASCII.GetBytes("test").CopyTo(main,groups+104);
        Encoding.ASCII.GetBytes("models\\split01.mdl").CopyTo(main,groups+104+32);
        Put(main,172,2);Put(main,176,groups);
        int seq=Read(main,168)+externalSequence*176;
        Put(main,seq+156,1);Put(main,seq+124,76);
        for(int off=180;off<=200;off+=4)Put(main,off,0);
        Put(main,72,main.Length);
        File.WriteAllBytes(DiskPath.Full(path),main);
    }
    static void Compare(Studio original,Studio candidate)
    {
        Check(original.Sequences.Count==candidate.Sequences.Count,"Sequences");
        Check(original.Textures.Count==candidate.Textures.Count,"Textures");
        for(int i=0;i<original.Textures.Count;i++) {
            var a=original.Textures[i];var b=candidate.Textures[i];
            Check(a.Flags==b.Flags && a.Width==b.Width && a.Height==b.Height &&
                BytesEqual(a.Pixels,b.Pixels) && BytesEqual(a.Palette,b.Palette),"Texture pixels / flags");
        }
        Check(original.Skins.Length==candidate.Skins.Length,"Skin families");
        for(int i=0;i<original.Skins.Length;i++)
            for(int j=0;j<original.Skins[i].Length;j++)Check(original.Skins[i][j]==candidate.Skins[i][j],"Skin");
        var testPoints=new V3[]{new V3(0,0,0),new V3(1,0,0),new V3(0,1,0),new V3(0,0,1)};
        for(int i=0;i<original.Sequences.Count;i++) {
            Sequence seq=original.Sequences[i];
            for(int blend=0;blend<seq.Blends;blend++)foreach(int frame in new int[]{0,seq.Frames/2,seq.Frames-1}) {
                var a=original.Pose(i,frame,blend);var b=candidate.Pose(i,frame,blend);
                for(int j=0;j<a.Length;j++)foreach(var point in testPoints)
                    Check(a[j].Point(point).Equals(b[j].Point(point)),"Bone pose differs");
            }
        }
    }
    static void Missing(string main,string file)
    {
        file=DiskPath.Full(file);
        string backup=DiskPath.Full(file+".hold");File.Move(file,backup);
        try {
            try { Studio.Load(main); }
            catch(FileNotFoundException) { return; }
            throw new Exception("A family with a missing companion was accepted.");
        } finally {File.Move(backup,file);}
    }
    static void CheckLongPaths(string work,string main,string tex,string group)
    {
        string parent=DiskPath.Display(DiskPath.Full(work));
        string root=Path.Combine(parent,"long-"+Guid.NewGuid().ToString("N"));
        string deep=root;
        while(deep.Length<300)deep=Path.Combine(deep,"modelos-em-uma-estrutura-de-pastas-comprida");
        try {
            Directory.CreateDirectory(DiskPath.Full(deep));
            foreach(string p in new string[]{main,tex,group})
                File.Copy(DiskPath.Full(p),DiskPath.Full(Path.Combine(deep,Path.GetFileName(p))));
            var read=Studio.Load(Path.Combine(deep,".","split.mdl"));
            Check(read.CompanionPaths.Count==2,"Long T / IDSQ paths");
            string dst=Path.Combine(deep,"autonomo.mdl");
            read.Save(dst,new Dictionary<Mesh,List<Tri>>());
            var result=Studio.Load(dst);
            Check(result.CompanionPaths.Count==0,"Long standalone output");
            read.VerifyMetadata(result);
            Compare(read,result);
        } finally {
            string tree=DiskPath.Tree(root);
            if(!String.Equals(Path.GetDirectoryName(DiskPath.Display(tree)), parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Diretório temporário inesperado.");
            if(Directory.Exists(tree))Directory.Delete(tree,true);
        }
    }
    public static int Main(string[] args)
    {
        try {
            Studio source=Studio.Load(args[0]);
            string work=Path.Combine(DiskPath.Full(args[1]),"family-"+Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(DiskPath.Full(work));
            string main=Path.Combine(work,"split.mdl"), tex=Path.Combine(work,"splitT.mdl"), group=Path.Combine(work,"split01.mdl");
            int seq=source.Sequences.Count-1;
            BuildMain(source,seq,main);BuildTextureFile(source,tex);BuildSequenceFile(source,seq,group);
            var loaded=Studio.Load(main);
            Check(loaded.CompanionPaths.Count==2,"Expected external T and 01");
            Compare(source,loaded);
            Missing(main,tex);Missing(main,group);
            CheckLongPaths(work,main,tex,group);
            string dest=Path.Combine(work,"autonomo.mdl");
            var mesh=loaded.Parts[0].Models[0].Meshes[0];
            var faces=new List<Tri>(mesh.Faces);faces.RemoveAt(faces.Count-1);
            loaded.Save(dest,new Dictionary<Mesh,List<Tri>>{{mesh,faces}},false);
            File.Delete(DiskPath.Full(tex));File.Delete(DiskPath.Full(group));
            var output=Studio.Load(dest);
            Check(output.CompanionPaths.Count==0,"Output still depends on another file");
            Check(output.TriangleCount==source.TriangleCount-1,"Geometry count");
            Compare(source,output);loaded.VerifyMetadata(output);
            Console.WriteLine("Família T + IDSQ consolidada. Texturas, skins, poses, componentes ausentes e caminhos de 300+ caracteres verificados.");
            return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
}
