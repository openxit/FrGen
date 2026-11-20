// Open-FrameGen Universal GPU Optical Flow & Motion-Compensated Interpolation
// Compatible with Direct3D 11 Feature Level 11_0+ (NVIDIA GTX 900+, AMD GCN+, Intel HD/Arc)

cbuffer FrameGenParams : register(b0)
{
    uint2 FrameSize;
    float Alpha;          // Interpolation factor in (0, 1), e.g., 0.5 for 2x
    int SearchRadius;     // 4 = Fast, 6 = Balanced, 8 = High
    float Sharpness;      // 0.0 = Off, > 0.0 = RCAS-style edge sharpening
    float3 _Pad;
};

Texture2D<float4> PrevFrame : register(t0);
Texture2D<float4> CurrFrame : register(t1);
Texture2D<float2> MotionVectorsIn : register(t2);

RWTexture2D<float2> MotionVectorsOut : register(u0);
RWTexture2D<float4> OutputFrame : register(u1);

float Luminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float4 SampleClamped(Texture2D<float4> tex, int2 coord)
{
    int2 c = clamp(coord, int2(0, 0), int2(FrameSize) - int2(1, 1));
    return tex.Load(int3(c, 0));
}

float4 SampleBilinear(Texture2D<float4> tex, float2 coord)
{
    float2 clamped = clamp(coord, float2(0.0, 0.0), float2(FrameSize) - float2(1.001, 1.001));
    int2 p0 = int2(floor(clamped));
    int2 p1 = min(p0 + int2(1, 1), int2(FrameSize) - int2(1, 1));
    float2 f = frac(clamped);

    float4 c00 = tex.Load(int3(p0.x, p0.y, 0));
    float4 c10 = tex.Load(int3(p1.x, p0.y, 0));
    float4 c01 = tex.Load(int3(p0.x, p1.y, 0));
    float4 c11 = tex.Load(int3(p1.x, p1.y, 0));

    return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
}

// Pass 1: Block-Matching Optical Flow (8x8 pixel blocks)
[numthreads(8, 8, 1)]
void CS_OpticalFlow(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    int2 blockCoord = int2(dispatchThreadId.xy);
    int2 blockOrigin = blockCoord * 8;
    if (blockOrigin.x >= int(FrameSize.x) || blockOrigin.y >= int(FrameSize.y))
        return;

    int radius = clamp(SearchRadius, 2, 10);
    float bestSad = 1e20;
    int2 bestOffset = int2(0, 0);

    // Coarse-to-fine search: step 2 then refine around best
    for (int dy = -radius; dy <= radius; dy += 2)
    {
        for (int dx = -radius; dx <= radius; dx += 2)
        {
            float sad = 0.0;
            [unroll]
            for (int py = 0; py < 8; py += 2)
            {
                [unroll]
                for (int px = 0; px < 8; px += 2)
                {
                    int2 currPos = blockOrigin + int2(px, py);
                    int2 prevPos = currPos - int2(dx, dy);
                    float lCurr = Luminance(SampleClamped(CurrFrame, currPos).rgb);
                    float lPrev = Luminance(SampleClamped(PrevFrame, prevPos).rgb);
                    sad += abs(lCurr - lPrev);
                }
            }

            if (sad < bestSad)
            {
                bestSad = sad;
                bestOffset = int2(dx, dy);
            }
        }
    }

    // 1-pixel refinement around best coarse candidate
    int2 coarseBest = bestOffset;
    for (int ry = -1; ry <= 1; ry++)
    {
        for (int rx = -1; rx <= 1; rx++)
        {
            int2 cand = coarseBest + int2(rx, ry);
            float sad = 0.0;
            [unroll]
            for (int py = 0; py < 8; py += 2)
            {
                [unroll]
                for (int px = 0; px < 8; px += 2)
                {
                    int2 currPos = blockOrigin + int2(px, py);
                    int2 prevPos = currPos - cand;
                    float lCurr = Luminance(SampleClamped(CurrFrame, currPos).rgb);
                    float lPrev = Luminance(SampleClamped(PrevFrame, prevPos).rgb);
                    sad += abs(lCurr - lPrev);
                }
            }

            if (sad < bestSad)
            {
                bestSad = sad;
                bestOffset = cand;
            }
        }
    }

    MotionVectorsOut[blockCoord] = float2(bestOffset);
}

// Pass 2: Bidirectional Motion-Compensated Frame Interpolation + Optional RCAS
[numthreads(8, 8, 1)]
void CS_Interpolate(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    int2 pixel = int2(dispatchThreadId.xy);
    if (pixel.x >= int(FrameSize.x) || pixel.y >= int(FrameSize.y))
        return;

    int2 blockCoord = pixel / 8;
    float2 mv = MotionVectorsIn.Load(int3(blockCoord, 0));

    float2 prevSamplePos = float2(pixel) - mv * Alpha;
    float2 currSamplePos = float2(pixel) + mv * (1.0 - Alpha);

    float4 prevCol = SampleBilinear(PrevFrame, prevSamplePos);
    float4 currCol = SampleBilinear(CurrFrame, currSamplePos);

    // Disocclusion / scene-cut guard
    float diff = length(prevCol.rgb - currCol.rgb);
    float blendAlpha = saturate(Alpha);
    float4 blended = lerp(prevCol, currCol, blendAlpha);

    if (diff > 0.65)
    {
        blended = lerp(SampleClamped(PrevFrame, pixel), SampleClamped(CurrFrame, pixel), blendAlpha);
    }

    if (Sharpness > 0.001)
    {
        float3 n = SampleClamped(CurrFrame, pixel + int2(0, -1)).rgb;
        float3 s = SampleClamped(CurrFrame, pixel + int2(0, 1)).rgb;
        float3 e = SampleClamped(CurrFrame, pixel + int2(1, 0)).rgb;
        float3 w = SampleClamped(CurrFrame, pixel + int2(-1, 0)).rgb;
        float3 avg = (n + s + e + w) * 0.25;
        blended.rgb = saturate(blended.rgb + (blended.rgb - avg) * Sharpness * 0.35);
    }

    OutputFrame[pixel] = float4(blended.rgb, 1.0);
}
