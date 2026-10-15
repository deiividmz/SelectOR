// SelectOR · mejora de imagen para Open Rails (efecto de ReShade). Código propio de SelectOR (© 2026 David MZP,
// GPL v3, como SelectOR). Lo configura SelectOR (Ajustes de SelectOR → Gráficos) en SelectOR-ReShade\SelectOR.ini;
// dentro de Open Rails se puede afinar con la tecla Inicio (menú de ReShade).
//  · (El suavizado de bordes es el SMAA de SweetFX y la viveza del color, su Vibrance: SMAA.fx y Vibrance.fx, antes que este.)
//  · Nitidez adaptativa: como CAS (afila menos donde ya hay contraste, sin halos), mezclada según «Nitidez».
//  · Color: brillo, contraste, saturación, calidez y viñeta.

uniform float Sharpen < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_label = "Nitidez"; > = 0.35;
uniform float Saturation < ui_type = "slider"; ui_min = -0.5; ui_max = 0.5; ui_label = "Saturacion"; > = 0.05;
uniform float Contrast < ui_type = "slider"; ui_min = -0.5; ui_max = 0.5; ui_label = "Contraste"; > = 0.05;
uniform float Brightness < ui_type = "slider"; ui_min = -0.5; ui_max = 0.5; ui_label = "Brillo"; > = 0.0;
uniform float Warmth < ui_type = "slider"; ui_min = -0.5; ui_max = 0.5; ui_label = "Calidez"; > = 0.0;
uniform float Vignette < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_label = "Vineta"; > = 0.15;

texture SelectOR_BackBufferTex : COLOR;
sampler SelectOR_BackBuffer { Texture = SelectOR_BackBufferTex; };

#define SOR_PX float2(BUFFER_RCP_WIDTH, BUFFER_RCP_HEIGHT)

void SOR_VS(in uint id : SV_VertexID, out float4 position : SV_Position, out float2 texcoord : TEXCOORD)
{
    texcoord.x = (id == 2) ? 2.0 : 0.0;
    texcoord.y = (id == 1) ? 2.0 : 0.0;
    position = float4(texcoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}

float SOR_Luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float3 SOR_At(float2 uv, float2 off) { return tex2D(SelectOR_BackBuffer, uv + off * SOR_PX).rgb; }

// ---- nitidez y color ----
float4 SOR_PS_Main(float4 vpos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float3 c = tex2D(SelectOR_BackBuffer, uv).rgb;

    if (Sharpen > 0.001)
    {
        float3 n = SOR_At(uv, float2(0.0, -1.0));
        float3 s = SOR_At(uv, float2(0.0, 1.0));
        float3 e = SOR_At(uv, float2(1.0, 0.0));
        float3 w = SOR_At(uv, float2(-1.0, 0.0));
        float3 mn = min(c, min(min(n, s), min(e, w)));
        float3 mx = max(c, max(max(n, s), max(e, w)));
        float3 amp = sqrt(saturate(min(mn, 1.0 - mx) / max(mx, 0.00001)));
        float3 wgt = -amp * 0.2;
        float3 sharp = saturate(((n + s + e + w) * wgt + c) / (1.0 + 4.0 * wgt));
        c = lerp(c, sharp, Sharpen);
    }

    c += Brightness * 0.25;
    c = (c - 0.5) * (1.0 + Contrast) + 0.5;
    float l = SOR_Luma(c);
    c = lerp(float3(l, l, l), c, 1.0 + Saturation);
    c += float3(0.06, 0.015, -0.06) * Warmth;

    if (Vignette > 0.001)
    {
        float2 d = uv - 0.5;
        d.x *= BUFFER_WIDTH * BUFFER_RCP_HEIGHT;
        c *= 1.0 - Vignette * 0.55 * smoothstep(0.25, 0.95, length(d));
    }
    return float4(saturate(c), 1.0);
}

technique SelectOR < ui_label = "SelectOR - imagen"; ui_tooltip = "Mejora de imagen de SelectOR para Open Rails"; >
{
    pass Color { VertexShader = SOR_VS; PixelShader = SOR_PS_Main; }
}
