using UnityEngine;

/// <summary>
/// Mountain-valley heightmap and splatmap for the VR arena. A pure function of
/// (seed, settings) — no scene access, no UnityEngine.Random — so the same seed always
/// rebuilds the same map, e.g. on every client of a future multiplayer session.
///
/// Shape, from the outside in: a ridged mountain rim that walls the map in, interior
/// ridges that give cover and break line of sight, gentle hills everywhere, flattened
/// valley floors to drive on, and a ridge-free central valley where the player starts.
/// </summary>
public static class MountainTerrainGenerator
{
    [System.Serializable]
    public class Settings
    {
        [Tooltip("Width and length of the square map, in meters.")]
        public float size = 36f;
        [Tooltip("World height of a heightmap value of 1, in meters. Nothing rises above this.")]
        public float maxHeight = 5.5f;
        [Tooltip("Heightmap samples per side. Unity requires 2^n + 1.")]
        public int heightmapResolution = 257;
        [Tooltip("Splatmap texels per side.")]
        public int alphamapResolution = 256;

        [Header("Rim")]
        [Tooltip("How far the boundary mountains reach in from the map edge, in meters.")]
        public float rimWidth = 6f;
        [Tooltip("Typical height of the boundary mountains, in meters.")]
        public float rimHeight = 3.6f;

        [Header("Ridges")]
        [Tooltip("Height of the tallest interior ridges, in meters.")]
        public float ridgeHeight = 2.5f;
        [Tooltip("Typical spacing between ridge crests, in meters.")]
        public float ridgeWavelength = 9f;
        [Tooltip("Scale of the noise that decides where ridges appear at all, in meters.")]
        public float ridgeMaskWavelength = 14f;
        [Tooltip("Higher = fewer ridges and more open valley.")]
        [Range(0f, 1f)] public float ridgeThreshold = 0.5f;
        [Tooltip("Above 1, flattens the foothills into wide valley floors while keeping the peaks.")]
        public float valleyFlattening = 1.6f;

        [Header("Hills")]
        [Tooltip("Amplitude of the gentle rolling hills everywhere, in meters.")]
        public float hillHeight = 0.35f;
        public float hillWavelength = 5f;

        [Header("Player start")]
        [Tooltip("Radius around the map center kept free of ridges, in meters.")]
        public float startClearRadius = 4f;
        [Tooltip("Distance past startClearRadius over which ridges fade back in, in meters.")]
        public float startBlendWidth = 4f;
    }

    // Splat layer order produced by ComputeSplatmap.
    public const int GrassLayer = 0;
    public const int DirtLayer = 1;
    public const int RockLayer = 2;
    public const int SnowLayer = 3;
    public const int LayerCount = 4;

    /// <summary>Heights as fractions of <see cref="Settings.maxHeight"/>, indexed [z, x] as TerrainData.SetHeights expects.</summary>
    public static float[,] GenerateHeights(int seed, Settings s)
    {
        var noise = new NoiseOffsets(seed);
        int res = s.heightmapResolution;
        float step = s.size / (res - 1);
        var heights = new float[res, res];

        for (int zi = 0; zi < res; zi++)
        {
            for (int xi = 0; xi < res; xi++)
                heights[zi, xi] = Mathf.Clamp01(SampleHeight(xi * step, zi * step, s, noise) / s.maxHeight);
        }

        return heights;
    }

    /// <summary>
    /// Layer weights from slope and altitude: snow on high ground (not sheer faces), rock on
    /// steep ground, dirt on transitional slopes and in patches, grass everywhere else.
    /// Indexed [z, x, layer] as TerrainData.SetAlphamaps expects.
    /// </summary>
    public static float[,,] ComputeSplatmap(TerrainData data, int seed, Settings s)
    {
        var noise = new NoiseOffsets(seed);
        int res = data.alphamapResolution;
        var maps = new float[res, res, LayerCount];

        for (int zi = 0; zi < res; zi++)
        {
            for (int xi = 0; xi < res; xi++)
            {
                float u = xi / (float)(res - 1);
                float v = zi / (float)(res - 1);
                float steepness = data.GetSteepness(u, v);
                float altitude = data.GetInterpolatedHeight(u, v) / s.maxHeight;
                float speckle = Fbm(new Vector2(u * s.size, v * s.size) / 2.5f, 2, noise.Splat);

                float snow = Smooth(0.6f, 0.72f, altitude + (speckle - 0.5f) * 0.12f) * (1f - Smooth(42f, 55f, steepness));
                float rock = Smooth(26f, 38f, steepness + (speckle - 0.5f) * 8f);
                float dirt = Mathf.Max(Smooth(16f, 26f, steepness) * 0.7f, Smooth(0.58f, 0.7f, speckle));

                // Layers claim what's left in priority order so the weights always sum to 1.
                float remaining = 1f;
                float wSnow = snow * remaining; remaining -= wSnow;
                float wRock = rock * remaining; remaining -= wRock;
                float wDirt = Mathf.Clamp01(dirt) * remaining; remaining -= wDirt;

                maps[zi, xi, SnowLayer] = wSnow;
                maps[zi, xi, RockLayer] = wRock;
                maps[zi, xi, DirtLayer] = wDirt;
                maps[zi, xi, GrassLayer] = remaining;
            }
        }

        return maps;
    }

    /// <summary>Height in meters at map-local (x, z), where (0, 0) is one corner and (size, size) the other.</summary>
    private static float SampleHeight(float x, float z, Settings s, NoiseOffsets noise)
    {
        Vector2 p = new Vector2(x, z);
        float half = s.size * 0.5f;

        // Gentle hills everywhere, around a base that keeps the valley floor above zero.
        float baseHeight = s.hillHeight + 0.05f;
        float hills = (Fbm(p / s.hillWavelength, 3, noise.Hills) - 0.5f) * 2f * s.hillHeight;

        // Interior ridges where the low-frequency mask allows, kept out of the start valley.
        float ridge = Ridged(p / s.ridgeWavelength, 4, noise.Ridge);
        float mask = Smooth(s.ridgeThreshold - 0.08f, s.ridgeThreshold + 0.08f, Fbm(p / s.ridgeMaskWavelength, 2, noise.Mask));
        float distanceFromCenter = Vector2.Distance(p, new Vector2(half, half));
        mask *= Smooth(s.startClearRadius, s.startClearRadius + s.startBlendWidth, distanceFromCenter);
        float ridges = s.ridgeHeight * Mathf.Pow(mask * ridge, s.valleyFlattening);

        // Boundary rim. The inner edge wobbles with noise so the arena isn't a visible square.
        float edgeDistance = Mathf.Min(Mathf.Min(x, s.size - x), Mathf.Min(z, s.size - z));
        edgeDistance += (Fbm(p / (s.rimWidth * 1.5f), 2, noise.RimEdge) - 0.5f) * s.rimWidth * 0.6f;
        float rimT = 1f - Smooth(0f, s.rimWidth, edgeDistance);
        float rimRidges = 0.7f + 0.6f * Ridged(p / (s.ridgeWavelength * 0.7f), 3, noise.RimRidge);
        float rim = s.rimHeight * Mathf.Pow(rimT, 1.3f) * rimRidges;

        return baseHeight + hills + Mathf.Max(ridges, rim);
    }

    /// <summary>Fractal Perlin noise in roughly [0, 1].</summary>
    private static float Fbm(Vector2 p, int octaves, Vector2 offset)
    {
        float sum = 0f, amplitude = 1f, frequency = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Perlin(p * frequency, offset, i) * amplitude;
            norm += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }
        return sum / norm;
    }

    /// <summary>
    /// Ridged multifractal in [0, 1]: folding the noise around its midpoint gives sharp
    /// crests, and each octave is weighted by the previous one so detail gathers on the
    /// ridges instead of roughening the valleys.
    /// </summary>
    private static float Ridged(Vector2 p, int octaves, Vector2 offset)
    {
        float sum = 0f, amplitude = 0.5f, frequency = 1f, weight = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float n = 1f - Mathf.Abs(Perlin(p * frequency, offset, i) * 2f - 1f);
            n *= n * weight;
            weight = Mathf.Clamp01(n * 2f);
            sum += n * amplitude;
            norm += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.1f;
        }
        return sum / norm;
    }

    private static float Perlin(Vector2 p, Vector2 offset, int octave)
    {
        // Per-octave shift so octaves don't share lattice points.
        return Mathf.PerlinNoise(p.x + offset.x + octave * 17.3f, p.y + offset.y + octave * 31.7f);
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    /// <summary>Seed-derived offsets that move each noise field to its own region of Perlin space.</summary>
    private readonly struct NoiseOffsets
    {
        public readonly Vector2 Hills, Ridge, Mask, RimEdge, RimRidge, Splat;

        public NoiseOffsets(int seed)
        {
            var rng = new System.Random(seed);
            Hills = Next(rng);
            Ridge = Next(rng);
            Mask = Next(rng);
            RimEdge = Next(rng);
            RimRidge = Next(rng);
            Splat = Next(rng);
        }

        private static Vector2 Next(System.Random rng) =>
            new Vector2((float)(rng.NextDouble() * 1000.0), (float)(rng.NextDouble() * 1000.0));
    }
}
