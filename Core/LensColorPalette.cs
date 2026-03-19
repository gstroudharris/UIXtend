// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Windows.UI;

namespace UIXtend.Core
{
    /// <summary>
    /// Deuteranomaly-friendly palette: 10 colors in OKLCH (L=0.65, C=0.18)
    /// traveling blue-violet → purple → magenta → orange → yellow, avoiding
    /// the green zone that trips up red-green colour-blind users.
    /// </summary>
    public static class LensColorPalette
    {
        // Hues in degrees: 265 (blue-violet) → 85 (yellow) via purple/magenta/orange
        private static readonly double[] Hues = { 265.0, 284.0, 303.0, 323.0, 342.0, 1.0, 21.0, 40.0, 60.0, 85.0 };

        public static readonly Color[] Colors;

        static LensColorPalette()
        {
            Colors = new Color[Hues.Length];
            for (int i = 0; i < Hues.Length; i++)
                Colors[i] = OklchToColor(0.65, 0.18, Hues[i]);
        }

        /// <summary>Returns the palette colour for the given zero-based index (wraps around).</summary>
        public static Color ForIndex(int index) =>
            Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];

        /// <summary>Returns black or white, whichever has better contrast against <paramref name="bg"/>.</summary>
        public static Color GetForeground(Color bg)
        {
            // sRGB relative luminance approximation
            double r = bg.R / 255.0, g = bg.G / 255.0, b = bg.B / 255.0;
            double lum = 0.2126 * LinearSrgb(r) + 0.7152 * LinearSrgb(g) + 0.0722 * LinearSrgb(b);
            return lum > 0.179 ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
                               : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }

        /// <summary>Darkens a colour by <paramref name="factor"/> (0–1 = darker).</summary>
        public static Color Darken(Color c, double factor) => Windows.UI.Color.FromArgb(
            c.A,
            (byte)Math.Clamp(c.R * factor, 0, 255),
            (byte)Math.Clamp(c.G * factor, 0, 255),
            (byte)Math.Clamp(c.B * factor, 0, 255));

        // ── OKLCH → linear sRGB → gamma-compressed sRGB ──────────────────────────

        private static Color OklchToColor(double L, double C, double hDeg)
        {
            double hRad = hDeg * Math.PI / 180.0;
            double a = C * Math.Cos(hRad);
            double b = C * Math.Sin(hRad);

            // OKLab → LMS (cube root space)
            double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
            double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
            double s_ = L - 0.0894841775 * a - 1.2914855480 * b;

            // LMS (cube root) → LMS (linear)
            double lc = l_ * l_ * l_;
            double mc = m_ * m_ * m_;
            double sc = s_ * s_ * s_;

            // LMS → linear sRGB
            double rLin =  4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
            double gLin = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
            double bLin = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;

            return Windows.UI.Color.FromArgb(
                255,
                ToSrgbByte(rLin),
                ToSrgbByte(gLin),
                ToSrgbByte(bLin));
        }

        private static byte ToSrgbByte(double c)
        {
            // Gamma-compress linear sRGB value and clamp to [0,255]
            c = Math.Clamp(c, 0.0, 1.0);
            double srgb = c <= 0.0031308
                ? 12.92 * c
                : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(srgb * 255.0), 0, 255);
        }

        private static double LinearSrgb(double c) =>
            c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
