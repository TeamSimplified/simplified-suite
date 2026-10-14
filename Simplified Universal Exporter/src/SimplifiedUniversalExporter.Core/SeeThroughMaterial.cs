using System;

namespace SimplifiedUniversalExporter.Core
{
    public enum SeeThroughKind
    {
        /// <summary>char_visor_skinned: a tinted visor, blended at me_alpha.</summary>
        Visor,

        /// <summary>char_visor_tear_skinned: the tear-off film on the visor, me_alpha 0.02 - all but invisible.</summary>
        TearOff,

        /// <summary>
        /// char_helmet_attachment (helmet_transparent): the clear plastic parts
        /// bolted to the shell - the wing, the hood scoop - refractive (IOR 1.4)
        /// and slightly rough, on the blank placeholder texture. The material
        /// carries no opacity of its own; <see cref="ClearPlasticOpacity"/> is
        /// the preview's stand-in for the refraction it can't draw.
        /// </summary>
        ClearPlastic,
    }

    /// <summary>
    /// How a see-through character material looks in game, decided from the
    /// material itself: its shader and its me_* values. A survey of every
    /// character archive in F1 25 found exactly these three helmet shaders
    /// see-through - every visor at me_alpha 0.9, every tear-off at 0.02, every
    /// helmet_transparent clear plastic at IOR 1.4 - and no other character
    /// shader carrying an opacity value.
    /// </summary>
    public sealed class SeeThroughMaterial
    {
        /// <summary>
        /// How solid clear plastic is drawn: enough to see the part's shape
        /// and the shell through it. Chosen by eye - the game refracts instead.
        /// </summary>
        public const float ClearPlasticOpacity = 0.3f;

        public SeeThroughKind Kind { get; private set; }

        /// <summary>How much of the surface's colour covers what is behind it, 0 to 1.</summary>
        public float Opacity { get; private set; }

        /// <summary>me_roughness, 0 = mirror smooth.</summary>
        public float Roughness { get; private set; }

        /// <summary>Index of refraction: me_indexOfRefraction or me_refractionIOR.</summary>
        public float Ior { get; private set; }

        /// <summary>The material's see-through response, or null for an opaque material.</summary>
        public static SeeThroughMaterial For(MaterialFile material)
        {
            string shader = material?.ShaderEaid;
            if (string.IsNullOrEmpty(shader)) return null;

            SeeThroughKind kind;
            if (Has(shader, "char_visor_tear")) kind = SeeThroughKind.TearOff;
            else if (Has(shader, "char_visor_skinned")) kind = SeeThroughKind.Visor;
            else if (Has(shader, "char_helmet_attachment")) kind = SeeThroughKind.ClearPlastic;
            else return null;

            float alpha = material.GetFloat("me_alpha") ?? 1f;
            return new SeeThroughMaterial
            {
                Kind = kind,
                Opacity = kind == SeeThroughKind.ClearPlastic ? ClearPlasticOpacity : Math.Max(0f, Math.Min(1f, alpha)),
                Roughness = Math.Max(0f, Math.Min(1f, material.GetFloat("me_roughness") ?? 0f)),
                Ior = material.GetFloat("me_indexOfRefraction") ?? material.GetFloat("me_refractionIOR") ?? 1.5f,
            };
        }

        private static bool Has(string s, string token) => s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
