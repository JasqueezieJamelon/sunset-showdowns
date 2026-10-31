using System;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    /// <summary>
    /// The street. Nothing here is bounded: the sky, the dirt and the town are generated across
    /// whatever slice of the world the camera is looking at, so a body can be driven as far down
    /// the road as its momentum carries it.
    /// </summary>
    public static class Scene
    {
        public const float GroundY = 905f;
        public static readonly Vector2 PlayerStand = new Vector2(470f, GroundY);
        public static readonly Vector2 EnemyStand = new Vector2(1400f, GroundY);

        const float BlockW = 620f;      // one building lot

        static Color C(int r, int g, int b, int a = 255) => new Color(r, g, b, a);

        public static void Update(float dt) { }

        /// <summary>Deterministic per-lot variation, so the town is the same every time you look.</summary>
        static float Hash(int i, int salt)
        {
            int h = i * 374761393 + salt * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }

        public static void Draw(float x0, float x1)
        {
            x0 -= 200f; x1 += 200f;
            float w = x1 - x0;

            // sky, taken high enough to cover any zoom
            Art.VGradient(new Rectangle(x0, -1200f, w, GroundY + 1204f), C(58, 36, 72), C(236, 152, 86), 60);
            Art.VGradient(new Rectangle(x0, GroundY - 320f, w, 324f), C(236, 152, 86, 0), C(252, 206, 132, 255), 24);

            Vector2 sun = new Vector2(960f, GroundY - 120f);
            if (sun.X > x0 - 400f && sun.X < x1 + 400f)
            {
                Raylib.DrawCircleV(sun, 150f, C(255, 226, 158, 60));
                Raylib.DrawCircleV(sun, 104f, C(255, 234, 176, 255));
            }

            Ridge(x0, x1, GroundY - 150f, 190f, C(118, 72, 86), 340f, 1.7f);
            Ridge(x0, x1, GroundY - 90f, 120f, C(92, 54, 70), 210f, 3.1f);

            Town(x0, x1);

            // dirt, deep enough that zooming out never finds its edge
            Art.VGradient(new Rectangle(x0, GroundY - 6f, w, 900f), C(186, 136, 92), C(96, 62, 48), 40);
            Raylib.DrawLineEx(new Vector2(x0, GroundY - 4f), new Vector2(x1, GroundY - 4f), 5f, C(142, 98, 66));

            int r0 = (int)MathF.Floor(x0 / 137.5f), r1 = (int)MathF.Ceiling(x1 / 137.5f);
            for (int i = r0; i <= r1; i++)
            {
                float x = i * 137.5f;
                float y = GroundY + 14f + (((i % 5) + 5) % 5) * 28f;
                Raylib.DrawEllipse((int)x, (int)y, 26f + (((i % 4) + 4) % 4) * 9f, 4f, C(112, 74, 52, 90));
            }
            for (int i = r0 * 2; i <= r1 * 2; i++)
            {
                float x = i * 73f;
                float y = GroundY + 10f + Hash(i, 11) * 150f;
                Raylib.DrawCircleV(new Vector2(x, y), 2.5f + Hash(i, 12) * 3f, C(140, 104, 74, 160));
            }

            Raylib.DrawRectangleRec(new Rectangle(x0, GroundY - 26f, w, 26f), C(248, 206, 150, 40));
        }

        static void Ridge(float x0, float x1, float baseY, float height, Color col, float step, float seed)
        {
            int i0 = (int)MathF.Floor(x0 / step) - 1, i1 = (int)MathF.Ceiling(x1 / step) + 1;
            int n = i1 - i0 + 1;
            var pts = new Vector2[n * 2 + 2];
            int k = 0;
            pts[k++] = new Vector2(i0 * step, baseY + 900f);
            for (int i = i0; i <= i1; i++)
            {
                float h = height * (0.45f + 0.55f * MathF.Abs(MathF.Sin(i * seed)));
                pts[k++] = new Vector2(i * step, baseY - h);
                pts[k++] = new Vector2(i * step + step * 0.5f, baseY - h * 0.35f);
            }
            pts[k++] = new Vector2(i1 * step, baseY + 900f);
            var arr = new Vector2[k];
            Array.Copy(pts, arr, k);
            Art.Poly(arr, col);
        }

        static void Town(float x0, float x1)
        {
            int i0 = (int)MathF.Floor(x0 / BlockW) - 1, i1 = (int)MathF.Ceiling(x1 / BlockW) + 1;
            for (int i = i0; i <= i1; i++)
            {
                // the duelling ground itself stays clear
                float lotX = i * BlockW;
                if (lotX > 560f && lotX < 1320f) continue;

                float wdt = 180f + Hash(i, 1) * 180f;
                float hgt = 170f + Hash(i, 2) * 180f;
                int tone = (int)(Hash(i, 3) * 3f);
                Color wall = tone switch
                {
                    0 => C(108, 74, 58),
                    1 => C(126, 88, 64),
                    _ => C(96, 66, 52),
                };
                Building(lotX + Hash(i, 4) * 70f, wdt, hgt, wall, Art.Shade(wall, 0.72f), Hash(i, 5) > 0.55f);

                if (Hash(i, 6) > 0.72f) WaterTower(lotX + 320f);
            }
        }

        static void WaterTower(float x)
        {
            Vector2 tb = new Vector2(x, GroundY - 300f);
            for (int i = -1; i <= 1; i += 2)
            {
                Raylib.DrawLineEx(tb + new Vector2(i * 46f, 0f), tb + new Vector2(i * 26f, 300f), 13f, C(70, 46, 38));
                Raylib.DrawLineEx(tb + new Vector2(i * 46f, 0f), tb + new Vector2(i * 36f, 150f), 9f, C(70, 46, 38));
            }
            Raylib.DrawLineEx(tb + new Vector2(-40f, 120f), tb + new Vector2(40f, 120f), 8f, C(70, 46, 38));
            Art.Shape(new[] { tb + new Vector2(-54f, 6f), tb + new Vector2(54f, 6f), tb + new Vector2(48f, -116f), tb + new Vector2(-48f, -116f) }, C(102, 70, 52), 3.5f);
            Art.Shape(new[] { tb + new Vector2(-64f, -116f), tb + new Vector2(64f, -116f), tb + new Vector2(0f, -168f) }, C(80, 54, 42), 3.5f);
        }

        static void Building(float x, float w, float h, Color wall, Color trim, bool porch)
        {
            float top = GroundY - h;
            Art.Shape(new[]
            {
                new Vector2(x, GroundY), new Vector2(x, top),
                new Vector2(x + w, top), new Vector2(x + w, GroundY)
            }, wall, 3f);

            Art.Shape(new[]
            {
                new Vector2(x - 8f, top), new Vector2(x + w + 8f, top),
                new Vector2(x + w + 8f, top - 26f), new Vector2(x - 8f, top - 26f)
            }, trim, 3f);

            int cols = Math.Max(1, (int)(w / 86f));
            for (int i = 0; i < cols; i++)
            {
                float wx = x + 26f + i * (w - 40f) / cols;
                Art.Shape(new[]
                {
                    new Vector2(wx, top + 54f), new Vector2(wx + 44f, top + 54f),
                    new Vector2(wx + 44f, top + 124f), new Vector2(wx, top + 124f)
                }, C(62, 48, 56), 3f);
                Raylib.DrawLineEx(new Vector2(wx, top + 88f), new Vector2(wx + 44f, top + 88f), 3f, trim);
            }

            if (porch)
            {
                float py = GroundY - 96f;
                Art.Shape(new[]
                {
                    new Vector2(x - 20f, py), new Vector2(x + w + 20f, py),
                    new Vector2(x + w + 14f, py - 18f), new Vector2(x - 14f, py - 18f)
                }, trim, 3f);
                for (int i = 0; i <= 2; i++)
                {
                    float px = x + 10f + i * (w - 20f) * 0.5f;
                    Raylib.DrawLineEx(new Vector2(px, py), new Vector2(px, GroundY - 2f), 9f, C(76, 52, 42));
                }
                Art.Shape(new[]
                {
                    new Vector2(x - 24f, GroundY - 2f), new Vector2(x + w + 24f, GroundY - 2f),
                    new Vector2(x + w + 24f, GroundY + 14f), new Vector2(x - 24f, GroundY + 14f)
                }, C(104, 72, 54), 3f);
            }
        }
    }
}
