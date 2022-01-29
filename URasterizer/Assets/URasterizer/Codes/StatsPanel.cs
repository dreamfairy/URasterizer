using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class StatsPanel : MonoBehaviour
{
    public Text TrianglesStat;
    public Text VerticesStat;

    public void StatDelegate(int vertices, int triangles, int trianglesRendered)
    {
        TrianglesStat.text = $"Triangles: {trianglesRendered} / {triangles}";
        VerticesStat.text = $"Vertices: {vertices}";
    }
}
