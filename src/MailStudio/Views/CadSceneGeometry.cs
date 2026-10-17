using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MailStudio.Views;

internal static class CadSceneGeometry
{
    static readonly int[] BoxIndices={0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7,0,3,2,0,2,1,4,5,6,4,6,7};
    public static Point3D Point(JsonElement p){var a=p.EnumerateArray().Select(x=>x.GetDouble()).ToArray();return new(a[0],a[1],a[2]);}
    public static double[] Array(Point3D p)=>new[]{p.X,p.Y,p.Z};
    public static MeshGeometry3D Mesh(JsonElement f)
    {
        var mesh=new MeshGeometry3D();var p=f.GetProperty("positions").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
        for(int i=0;i<p.Length;i+=3)mesh.Positions.Add(new(p[i],p[i+1],p[i+2]));
        foreach(var n in f.GetProperty("indices").EnumerateArray())mesh.TriangleIndices.Add(n.GetInt32());mesh.Freeze();return mesh;
    }
    public static GeometryModel3D Model(MeshGeometry3D mesh,Brush brush)
    {
        var material=new DiffuseMaterial(brush);return new(mesh,material){BackMaterial=material};
    }
    public static void Rod(MeshGeometry3D mesh,Point3D a,Point3D b,double radius)
    {
        var d=b-a;if(d.Length<1e-12)return;var u=Vector3D.CrossProduct(d,Math.Abs(d.Z/d.Length)<.9?new Vector3D(0,0,1):new Vector3D(1,0,0));u.Normalize();u*=radius;
        var v=Vector3D.CrossProduct(d,u);v.Normalize();v*=radius;int start=mesh.Positions.Count;
        foreach(var p in new[]{a,b})foreach(var s in new[]{(-1,-1),(-1,1),(1,1),(1,-1)})mesh.Positions.Add(p+u*s.Item1+v*s.Item2);
        foreach(var i in BoxIndices)mesh.TriangleIndices.Add(start+i);
    }
    public static void Marker(MeshGeometry3D mesh,Point3D p,double radius)
    {
        int start=mesh.Positions.Count;
        foreach(var z in new[]{-1,1})foreach(var xy in new[]{(-1,-1),(-1,1),(1,1),(1,-1)})mesh.Positions.Add(p+new Vector3D(xy.Item1*radius,xy.Item2*radius,z*radius));
        foreach(var i in BoxIndices)mesh.TriangleIndices.Add(start+i);
    }
    public static Point Project(Point3D p,OrthographicCamera camera,double width,double height)
    {
        var look=camera.LookDirection;look.Normalize();var right=Vector3D.CrossProduct(look,camera.UpDirection);right.Normalize();var up=Vector3D.CrossProduct(right,look);var d=p-camera.Position;
        return new(width/2+Vector3D.DotProduct(d,right)*width/camera.Width,height/2-Vector3D.DotProduct(d,up)*width/camera.Width);
    }
}
