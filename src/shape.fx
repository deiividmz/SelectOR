// Réplica del shader StandardFog del Shape Viewer de TSRE5 (sin niebla/sombras/2ª textura).
// Modelo de luz: texColor * (ambient + diffuse * saturate(N·sol)).  Alfa por primitiva (VAlpha):
//   opaco = 1, recorte (alpha-test) = -0.51, blend = 0.   discard si  a < -VAlpha  con  a = max(texA, VAlpha).

#if OPENGL
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0
    #define PS_SHADERMODEL ps_4_0
#endif

matrix WorldViewProjection;
matrix WorldView;      // normales al espacio de vista (luz relativa a la cámara, como TSRE5)
float3 SunDir;         // dirección de luz normalizada (espacio de vista)
float3 Ambient;        // (0.3,0.3,0.3)
float3 Diffuse;        // (0.7,0.7,0.7)
float  VAlpha;         // por primitiva: 1 opaco, -0.51 recorte, 0 blend

Texture2D Tex;
sampler2D TexSampler = sampler_state { Texture = <Tex>; };

struct VSIn {
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 TexCoord : TEXCOORD0;
};
struct VSOut {
    float4 Position : SV_POSITION;
    float3 Normal   : TEXCOORD0;
    float2 TexCoord : TEXCOORD1;
};

VSOut MainVS(VSIn v) {
    VSOut o;
    o.Position = mul(v.Position, WorldViewProjection);
    o.Normal   = mul(float4(v.Normal, 0), WorldView).xyz;
    o.TexCoord = v.TexCoord;
    return o;
}

float4 MainPS(VSOut i) : COLOR0 {
    float4 c = tex2D(TexSampler, i.TexCoord);
    float a = max(c.a, VAlpha);
    clip(a + VAlpha);                       // discard si a < -VAlpha
    float cosT = saturate(dot(normalize(i.Normal), SunDir));
    c.rgb *= Diffuse * cosT + Ambient;
    c.a = a;
    return c;
}

technique Shape {
    pass P0 {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
