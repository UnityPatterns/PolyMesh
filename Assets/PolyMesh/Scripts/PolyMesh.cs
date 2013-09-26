using UnityEngine;
using System.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter))]
public class PolyMesh : MonoBehaviour
{
	public List<Vector3> keyPoints = new List<Vector3>();
	public List<Vector3> curvePoints = new List<Vector3>();
	public List<bool> isCurve = new List<bool>();
	public MeshCollider meshCollider;
	[Range(0.01f, 1)] public float curveDetail = 0.1f;
	public float colliderDepth = 1;
	public bool buildColliderEdges = true;
	public bool buildColliderFront;
	public Vector2 uvPosition;
	public float uvScale = 1;
	public float uvRotation;
	
	public List<Vector3> GetEdgePoints()
	{
		//Build the point list and calculate curves
		var points = new List<Vector3>();
		for (int i = 0; i < keyPoints.Count; i++)
		{
			if (isCurve[i])
			{
				//Get the curve control point
				var a = keyPoints[i];
				var c = keyPoints[(i + 1) % keyPoints.Count];
				var b = Bezier.Control(a, c, curvePoints[i]);
				
				//Build the curve
				var count = Mathf.Ceil(1 / curveDetail);
				for (int j = 0; j < count; j++)
				{
					var t = (float)j / count;
					points.Add(Bezier.Curve(a, b, c, t));
				}
			}
			else
				points.Add(keyPoints[i]);
		}
		return points;
	}
	
	public void BuildMesh()
	{
		var points = GetEdgePoints();
		var vertices = points.ToArray();
		
		//Build the index array
		var indices = new List<int>();
		while (indices.Count < points.Count)
			indices.Add(indices.Count);

		//Build the triangle array
		var triangles = Triangulate.Points(points);
		
		//Build the uv array
		var scale = uvScale != 0 ? (1 / uvScale) : 0;
		var matrix = Matrix4x4.TRS(-uvPosition, Quaternion.Euler(0, 0, uvRotation), new Vector3(scale, scale, 1));
		var uv = new Vector2[points.Count];
		for (int i = 0; i < uv.Length; i++)
		{
			var p = matrix.MultiplyPoint(points[i]);
			uv[i] = new Vector2(p.x, p.y);
		}
		
		//Find the mesh (create it if it doesn't exist)
		var meshFilter = GetComponent<MeshFilter>();
		var mesh = meshFilter.sharedMesh;
		if (mesh == null)
		{
			mesh = new Mesh();
			mesh.name = "PolySprite_Mesh";
			meshFilter.mesh = mesh;
		}
		
		//Update the mesh
		mesh.Clear();
		mesh.vertices = vertices;
		mesh.uv = uv;
		mesh.triangles = triangles;
		mesh.RecalculateNormals();
		mesh.Optimize();
		
		//Update collider after the mesh is updated
		UpdateCollider(points, triangles);
	}
	
	void UpdateCollider(List<Vector3> points, int[] tris)
	{
		//Update the mesh collider if there is one
		if (meshCollider != null)
		{
			var vertices = new List<Vector3>();
			var triangles = new List<int>();
			
			if (buildColliderEdges)
			{
				//Build vertices array
				var offset = new Vector3(0, 0, colliderDepth / 2);
				for (int i = 0; i < points.Count; i++)
				{
					vertices.Add(points[i] + offset);
					vertices.Add(points[i] - offset);
				}
				
				//Build triangles array
				for (int a = 0; a < vertices.Count; a += 2)
				{
					var b = (a + 1) % vertices.Count;
					var c = (a + 2) % vertices.Count;
					var d = (a + 3) % vertices.Count;
					triangles.Add(a);
					triangles.Add(c);
					triangles.Add(b);
					triangles.Add(c);
					triangles.Add(d);
					triangles.Add(b);
				}
			}
			
			if (buildColliderFront)
			{
				for (int i = 0; i < tris.Length; i++)
					tris[i] += vertices.Count;
				vertices.AddRange(points);
				triangles.AddRange(tris);
			}
			
			//Find the mesh (create it if it doesn't exist)
			var mesh = meshCollider.sharedMesh;
			if (mesh == null)
			{
				mesh = new Mesh();
				mesh.name = "PolySprite_Collider";
			}
			
			//Update the mesh
			mesh.Clear();
			mesh.vertices = vertices.ToArray();
			mesh.triangles = triangles.ToArray();
			mesh.RecalculateNormals();
			mesh.Optimize();
			meshCollider.sharedMesh = null;
			meshCollider.sharedMesh = mesh;
		}
	}
	
	bool IsRightTurn(List<Vector3> points, int a, int b, int c)
	{
		var ab = points[b] - points[a];
		var bc = points[c] - points[b];
		return (ab.x * bc.y - ab.y * bc.x) < 0;
	}
	
	bool IntersectsExistingLines(List<Vector3> points, Vector3 a, Vector3 b)
	{
		for (int i = 0; i < points.Count; i++)
			if (LinesIntersect(points, a, b, points[i], points[(i + 1) % points.Count]))
				return true;
		return false;
	}
	
	bool LinesIntersect(List<Vector3> points, Vector3 point1, Vector3 point2, Vector3 point3, Vector3 point4)
	{
		if (point1 == point3 || point1 == point4 || point2 == point3 || point2 == point4)
			return false;
		
		float ua = (point4.x - point3.x) * (point1.y - point3.y) - (point4.y - point3.y) * (point1.x - point3.x);
		float ub = (point2.x - point1.x) * (point1.y - point3.y) - (point2.y - point1.y) * (point1.x - point3.x);
		float denominator = (point4.y - point3.y) * (point2.x - point1.x) - (point4.x - point3.x) * (point2.y - point1.y);
		
		if (Mathf.Abs(denominator) <= 0.00001f)
		{
			if (Mathf.Abs(ua) <= 0.00001f && Mathf.Abs(ub) <= 0.00001f)
				return true;
		}
		else
		{
			ua /= denominator;
			ub /= denominator;
			
			if (ua >= 0 && ua <= 1 && ub >= 0 && ub <= 1)
				return true;
		}
		
		return false;
	}
}

public static class Bezier
{
	public static float Curve(float from, float control, float to, float t)
	{
		return from * (1 - t) * (1 - t) + control * 2 * (1 - t) * t + to * t * t;
	}
	public static Vector3 Curve(Vector3 from, Vector3 control, Vector3 to, float t)
	{
		from.x = Curve(from.x, control.x, to.x, t);
		from.y = Curve(from.y, control.y, to.y, t);
		from.z = Curve(from.z, control.z, to.z, t);
		return from;
	}
	
	public static Vector3 Control(Vector3 from, Vector3 to, Vector3 curve)
	{
		//var center = Vector3.Lerp(from, to, 0.5f);
		//return center + (curve - center) * 2;
		var axis = Vector3.Normalize(to - from);
		var dot = Vector3.Dot(axis, curve - from);
		var linePoint = from + axis * dot;
		return linePoint + (curve - linePoint) * 2;
	}
}

public static class Triangulate
{
	public static int[] Points(List<Vector3> points)
	{
		var indices = new List<int>();
		
		int n = points.Count;
		if (n < 3)
			return indices.ToArray();
		
		int[] V = new int[n];
		if (Area(points) > 0)
		{
			for (int v = 0; v < n; v++)
				V[v] = v;
		}
		else
		{
			for (int v = 0; v < n; v++)
				V[v] = (n - 1) - v;
		}
		
		int nv = n;
		int count = 2 * nv;
		for (int m = 0, v = nv - 1; nv > 2; )
		{
			if ((count--) <= 0)
				return indices.ToArray();
			
			int u = v;
			if (nv <= u)
				u = 0;
			v = u + 1;
			if (nv <= v)
				v = 0;
			int w = v + 1;
			if (nv <= w)
				w = 0;
			
			if (Snip(points, u, v, w, nv, V))
			{
				int a, b, c, s, t;
				a = V[u];
				b = V[v];
				c = V[w];
				indices.Add(a);
				indices.Add(b);
				indices.Add(c);
				m++;
				for (s = v, t = v + 1; t < nv; s++, t++)
					V[s] = V[t];
				nv--;
				count = 2 * nv;
			}
		}
		
		indices.Reverse();
		return indices.ToArray();
	}
	
	static float Area(List<Vector3> points)
	{
		int n = points.Count;
		float A = 0.0f;
		for (int p = n - 1, q = 0; q < n; p = q++)
		{
			Vector3 pval = points[p];
			Vector3 qval = points[q];
			A += pval.x * qval.y - qval.x * pval.y;
		}
		return (A * 0.5f);
	}
	
	static bool Snip(List<Vector3> points, int u, int v, int w, int n, int[] V)
	{
		int p;
		Vector3 A = points[V[u]];
		Vector3 B = points[V[v]];
		Vector3 C = points[V[w]];
		if (Mathf.Epsilon > (((B.x - A.x) * (C.y - A.y)) - ((B.y - A.y) * (C.x - A.x))))
			return false;
		for (p = 0; p < n; p++)
		{
			if ((p == u) || (p == v) || (p == w))
				continue;
			Vector3 P = points[V[p]];
			if (InsideTriangle(A, B, C, P))
				return false;
		}
		return true;
	}
	
	static bool InsideTriangle(Vector2 A, Vector2 B, Vector2 C, Vector2 P)
	{
		float ax, ay, bx, by, cx, cy, apx, apy, bpx, bpy, cpx, cpy;
		float cCROSSap, bCROSScp, aCROSSbp;
		
		ax = C.x - B.x; ay = C.y - B.y;
		bx = A.x - C.x; by = A.y - C.y;
		cx = B.x - A.x; cy = B.y - A.y;
		apx = P.x - A.x; apy = P.y - A.y;
		bpx = P.x - B.x; bpy = P.y - B.y;
		cpx = P.x - C.x; cpy = P.y - C.y;
		
		aCROSSbp = ax * bpy - ay * bpx;
		cCROSSap = cx * apy - cy * apx;
		bCROSScp = bx * cpy - by * cpx;
		
		return ((aCROSSbp >= 0.0f) && (bCROSScp >= 0.0f) && (cCROSSap >= 0.0f));
	}
}
