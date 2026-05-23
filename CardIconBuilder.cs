// Compact "card icon": portrait art clipped to the card-type silhouette,
// the rarity-tinted PortraitBorder ring on top, and the small ribbon
// banner perched above. No description box, no cost, no full Frame.
//
// Two textures, two roles:
//   • CardModel.PortraitBorder — the visible ring around the art
//     window. Recoloured by rarity via a luminance shader so the
//     teal/gold base doesn't muddy the tint.
//   • run_history/<type>_portrait.png — a standalone (non-atlas) PNG
//     of the silhouette. We never *show* this one; it lives only as a
//     `sampler2D` mask. Atlas textures don't sample correctly through
//     shader uniforms (the region info is lost), which is why we
//     can't reuse PortraitBorder for both roles.
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace RunTable;

public static class CardIconBuilder
{
    private const string AttackShapePath = "res://images/packed/run_history/attack_portrait.png";
    private const string SkillShapePath  = "res://images/packed/run_history/skill_portrait.png";
    private const string PowerShapePath  = "res://images/packed/run_history/power_portrait.png";
    private const string BannerPath      = "res://images/packed/run_history/banner.png";

    // Banner extends past the icon's sides — the user wanted a pennant
    // that hangs over the edges rather than a strip that fits inside.
    private const float BannerSideOverhang = 0.18f;
    private const float BannerTop          = 0.00f;
    private const float BannerBottom       = 0.36f;

    // run_history silhouette is tighter than the PortraitBorder ring,
    // so the clipped portrait lands well inside the ring with empty
    // air between. Overscale is per-type because each texture sits in
    // its source PNG differently — skills have more empty space below
    // the flat bottom than power's curve does, and attacks' sharp
    // corners need their own tuning.
    private static (float H, float V) ClipOverscaleFor(CardTypeShape t) => t switch
    {
        CardTypeShape.Power  => (0.15f, 0.30f),
        CardTypeShape.Skill  => (0.15f, 0.75f),
        CardTypeShape.Attack => (0.45f, 0.25f),
        _                    => (0.15f, 0.30f),
    };

    public static Control Build(CardModel? card, float size)
    {
        var root = new Control
        {
            CustomMinimumSize = new Vector2(size, size),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        if (card == null) return root;

        var cardType = GetCardType(card);
        var maskTex = LoadShapeTexture(cardType);
        var rarity = ColorForRarity(card.Rarity);
        var (clipOverscaleH, clipOverscaleV) = ClipOverscaleFor(cardType);

        // Rect-clip wrapper. Attacks need a heavy horizontal overscale
        // so the silhouette's rounded sides land past the visible icon
        // edges — but that leaves the silhouette/portrait draw rect
        // hanging off the icon, which can leak through anti-aliasing
        // (and is what was "spilling out the left side"). The wrapper
        // hard-clips everything to the icon's bounds. Banner is added
        // to `root` directly so its pennant overhang isn't clipped
        // along with the rest.
        var iconClip = new Control
        {
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        FillRect(iconClip);
        root.AddChild(iconClip);

        // 1) Portrait — clipped to the run-history silhouette so the
        //    art doesn't poke past the curved bottom. We do this by
        //    making the silhouette TextureRect a CanvasItem clipper
        //    (`ClipChildren = Only` masks its children to wherever it
        //    draws non-transparent pixels) and parenting the portrait
        //    underneath. The silhouette itself isn't visible.
        if (card.Portrait != null && maskTex != null)
        {
            var clipper = new TextureRect
            {
                Texture = maskTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ClipChildren = CanvasItem.ClipChildrenMode.Only,
            };
            clipper.AnchorLeft = -clipOverscaleH; clipper.AnchorRight  = 1f + clipOverscaleH;
            clipper.AnchorTop  = -clipOverscaleV; clipper.AnchorBottom = 1f + clipOverscaleV;
            clipper.OffsetLeft = 0; clipper.OffsetRight = 0;
            clipper.OffsetTop  = 0; clipper.OffsetBottom = 0;

            // Portrait is parented to the (oversized) clipper for the
            // mask to apply, but we anchor it back to the icon's own
            // bounds inside clipper-space so it doesn't get stretched
            // along with the silhouette. The fractions below put the
            // portrait at exactly the root rect in screen space.
            float clipperW = 1f + 2f * clipOverscaleH;
            float clipperH = 1f + 2f * clipOverscaleV;
            var portrait = new TextureRect
            {
                Texture = card.Portrait,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            portrait.AnchorLeft  = clipOverscaleH / clipperW;
            portrait.AnchorRight = (1f + clipOverscaleH) / clipperW;
            portrait.AnchorTop   = clipOverscaleV / clipperH;
            portrait.AnchorBottom = (1f + clipOverscaleV) / clipperH;
            portrait.OffsetLeft = 0; portrait.OffsetRight = 0;
            portrait.OffsetTop  = 0; portrait.OffsetBottom = 0;

            clipper.AddChild(portrait);
            iconClip.AddChild(clipper);
        }
        else if (card.Portrait != null)
        {
            var portrait = new TextureRect
            {
                Texture = card.Portrait,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            FillRect(portrait);
            iconClip.AddChild(portrait);
        }

        // 2) PortraitBorder — recoloured by rarity (luminance × tint
        //    drops the source texture's hue so multiplying by gold
        //    doesn't read as green).
        if (card.PortraitBorder != null)
        {
            var border = new TextureRect
            {
                Texture = card.PortraitBorder,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Material = BuildRecolorMaterial(rarity, brightness: 1.6f),
            };
            FillRect(border);
            iconClip.AddChild(border);
        }

        // 3) Banner — perched on top, wider than the icon, rarity-
        //    tinted to match the ring colour.
        var bannerTex = ResourceLoader.Load<Texture2D>(BannerPath);
        if (bannerTex != null)
        {
            var banner = new TextureRect
            {
                Texture = bannerTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = rarity,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            banner.AnchorLeft = 0; banner.AnchorRight = 1;
            banner.AnchorTop = 0; banner.AnchorBottom = 0;
            banner.OffsetLeft  = -size * BannerSideOverhang;
            banner.OffsetRight =  size * BannerSideOverhang;
            banner.OffsetTop    = size * BannerTop;
            banner.OffsetBottom = size * BannerBottom;
            root.AddChild(banner);
        }

        return root;
    }

    private static void FillRect(Control c)
    {
        c.AnchorLeft = 0; c.AnchorRight = 1;
        c.AnchorTop = 0; c.AnchorBottom = 1;
        c.OffsetLeft = 0; c.OffsetRight = 0;
        c.OffsetTop = 0; c.OffsetBottom = 0;
    }

    // Luminance × tint: drops the source texture's hue so multiplying
    // by gold doesn't read as green on a teal-baked PortraitBorder.
    private static ShaderMaterial BuildRecolorMaterial(Color tint, float brightness)
    {
        var shader = new Shader();
        shader.Code = @"
shader_type canvas_item;
uniform vec3 tint : source_color = vec3(1.0);
uniform float brightness : hint_range(0.0, 4.0) = 1.0;
void fragment() {
    vec4 tex = texture(TEXTURE, UV);
    float lum = dot(tex.rgb, vec3(0.299, 0.587, 0.114));
    COLOR = vec4(tint * lum * brightness, tex.a);
}
";
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("tint", new Vector3(tint.R, tint.G, tint.B));
        mat.SetShaderParameter("brightness", brightness);
        return mat;
    }

    private enum CardTypeShape { Attack, Skill, Power }

    private static CardTypeShape GetCardType(CardModel card)
    {
        try
        {
            var prop = card.GetType().GetProperty("Type",
                BindingFlags.Instance | BindingFlags.Public);
            if (prop != null)
            {
                var val = prop.GetValue(card)?.ToString() ?? "";
                if (val.Contains("Attack")) return CardTypeShape.Attack;
                if (val.Contains("Power"))  return CardTypeShape.Power;
            }
        }
        catch { }
        return CardTypeShape.Skill;
    }

    private static Texture2D? LoadShapeTexture(CardTypeShape t) => t switch
    {
        CardTypeShape.Attack => ResourceLoader.Load<Texture2D>(AttackShapePath),
        CardTypeShape.Power  => ResourceLoader.Load<Texture2D>(PowerShapePath),
        _                    => ResourceLoader.Load<Texture2D>(SkillShapePath),
    };

    // Hand-tuned palette that matches the user-described rarity bands.
    // Basic / colorless rounds back to common grey; Token + None fall
    // back to white so they at least render.
    private static Color ColorForRarity(CardRarity rarity) => rarity switch
    {
        CardRarity.Common   => new Color(0.82f, 0.82f, 0.82f),
        CardRarity.Basic    => new Color(0.82f, 0.82f, 0.82f),
        CardRarity.Uncommon => new Color(0.40f, 0.65f, 0.95f),
        CardRarity.Rare     => new Color(1.00f, 0.85f, 0.30f),
        CardRarity.Curse    => new Color(0.70f, 0.40f, 0.85f),
        CardRarity.Status   => new Color(0.85f, 0.75f, 0.55f),
        CardRarity.Event    => new Color(0.50f, 0.85f, 0.50f),
        CardRarity.Quest    => new Color(0.95f, 0.65f, 0.30f),
        CardRarity.Ancient  => new Color(0.95f, 0.85f, 0.55f),
        _                   => Colors.White,
    };
}
