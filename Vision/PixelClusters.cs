using System.Drawing;

namespace HappyBot.Vision;

/// <summary>
/// One group of matching pixels. First is the topmost-then-leftmost pixel,
/// Center is the rounded mean position and Bounds covers every pixel.
/// </summary>
internal readonly record struct PixelCluster(Point First, Point Center, Rectangle Bounds, int PixelCount);

/// <summary>
/// Groups matching pixels into clusters. Pixels are bucketed into square cells
/// and cells that touch (including diagonally) join one cluster, so a gap
/// smaller than one cell never splits a cluster and a gap of two cells always
/// does. A cell size of 1 is plain 8-connectivity.
/// </summary>
internal static class PixelClusters
{
    public static List<PixelCluster> Group(IReadOnlyList<Point> pixels, int cellSize)
    {
        var clusters = new List<PixelCluster>();
        if (pixels == null || pixels.Count == 0) return clusters;
        cellSize = Math.Max(1, cellSize);

        var cellIndex = new Dictionary<long, int>(pixels.Count);
        var pixelCell = new int[pixels.Count];
        for (int i = 0; i < pixels.Count; i++)
        {
            long key = CellKey(FloorDiv(pixels[i].X, cellSize), FloorDiv(pixels[i].Y, cellSize));
            if (!cellIndex.TryGetValue(key, out int index))
            {
                index = cellIndex.Count;
                cellIndex.Add(key, index);
            }
            pixelCell[i] = index;
        }

        var parent = new int[cellIndex.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        foreach (KeyValuePair<long, int> cell in cellIndex)
        {
            int cellX = (int)(cell.Key >> 32);
            int cellY = (int)cell.Key;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0 && dy == 0) ||
                        !cellIndex.TryGetValue(CellKey(cellX + dx, cellY + dy), out int neighbor))
                        continue;
                    int a = Find(parent, cell.Value), b = Find(parent, neighbor);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
            }
        }

        var rootCluster = new Dictionary<int, int>();
        var counts = new List<int>();
        var sumX = new List<long>();
        var sumY = new List<long>();
        var first = new List<Point>();
        var minimum = new List<Point>();
        var maximum = new List<Point>();
        for (int i = 0; i < pixels.Count; i++)
        {
            Point pixel = pixels[i];
            int root = Find(parent, pixelCell[i]);
            if (!rootCluster.TryGetValue(root, out int c))
            {
                c = counts.Count;
                rootCluster.Add(root, c);
                counts.Add(0);
                sumX.Add(0);
                sumY.Add(0);
                first.Add(pixel);
                minimum.Add(pixel);
                maximum.Add(pixel);
            }
            counts[c]++;
            sumX[c] += pixel.X;
            sumY[c] += pixel.Y;
            if (pixel.Y < first[c].Y || pixel.Y == first[c].Y && pixel.X < first[c].X) first[c] = pixel;
            minimum[c] = new Point(Math.Min(minimum[c].X, pixel.X), Math.Min(minimum[c].Y, pixel.Y));
            maximum[c] = new Point(Math.Max(maximum[c].X, pixel.X), Math.Max(maximum[c].Y, pixel.Y));
        }

        for (int c = 0; c < counts.Count; c++)
        {
            var center = new Point((int)Math.Round(sumX[c] / (double)counts[c]),
                (int)Math.Round(sumY[c] / (double)counts[c]));
            clusters.Add(new PixelCluster(first[c], center,
                Rectangle.FromLTRB(minimum[c].X, minimum[c].Y, maximum[c].X + 1, maximum[c].Y + 1), counts[c]));
        }
        return clusters;
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }
        return index;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static long CellKey(int x, int y) => ((long)x << 32) | (uint)y;
}
