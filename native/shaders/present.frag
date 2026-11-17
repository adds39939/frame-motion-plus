#version 450

// Presenter pass: scale the staged ring frame into the content rectangle, then apply the output transform.
// Both stages are selected by specialization constants, so a new scaler or transform is a new constant value and
// function here plus a pipeline variant on the C# side (OutputTransform / PresentScaler), not a new pass.

// 0 = hardware bilinear. Reserved for later: 1 = bicubic, 2 = Lanczos (manual taps, same inputs).
layout(constant_id = 0) const int SCALER = 0;

// 0 = passthrough (8-bit SDR to SDR, 10-bit PQ to an HDR10 ST 2084 target).
// 1 = PLACEHOLDER PQ to SDR: decode PQ, put 203 nits at SDR white, hard clip, encode sRGB. No gamut mapping,
//     no tone curve. Phase 7 replaces it with real tone mapping and adds SDR-in-HDR.
layout(constant_id = 1) const int TRANSFORM = 0;

layout(set = 0, binding = 0) uniform sampler2D uSource;

layout(push_constant) uniform PushConstants
{
    vec2 rectOrigin; // content rectangle in target pixels
    vec2 rectSize;
    int identity;    // 1 when the rectangle is exactly the source size: texel-exact fetch, no filtering
} pc;

layout(location = 0) out vec4 outColor;

vec3 scaleSample(vec2 local)
{
    if (pc.identity != 0)
    {
        return texelFetch(uSource, ivec2(local), 0).rgb;
    }

    vec2 uv = local / pc.rectSize;
    return textureLod(uSource, uv, 0.0).rgb;
}

vec3 pqToNits(vec3 e)
{
    const float m1 = 2610.0 / 16384.0;
    const float m2 = 2523.0 / 4096.0 * 128.0;
    const float c1 = 3424.0 / 4096.0;
    const float c2 = 2413.0 / 4096.0 * 32.0;
    const float c3 = 2392.0 / 4096.0 * 32.0;
    vec3 p = pow(clamp(e, 0.0, 1.0), vec3(1.0 / m2));
    vec3 l = pow(max(p - c1, 0.0) / (c2 - c3 * p), vec3(1.0 / m1));
    return l * 10000.0;
}

vec3 linearToSrgb(vec3 l)
{
    vec3 lo = l * 12.92;
    vec3 hi = 1.055 * pow(l, vec3(1.0 / 2.4)) - 0.055;
    return mix(hi, lo, lessThanEqual(l, vec3(0.0031308)));
}

vec3 outputTransform(vec3 c)
{
    if (TRANSFORM == 1)
    {
        return linearToSrgb(clamp(pqToNits(c) / 203.0, 0.0, 1.0));
    }

    return c;
}

void main()
{
    vec2 local = gl_FragCoord.xy - pc.rectOrigin;
    outColor = vec4(outputTransform(scaleSample(local)), 1.0);
}
