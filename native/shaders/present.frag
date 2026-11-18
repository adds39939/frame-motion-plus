#version 450

// Presenter pass: scale the staged ring frame into the content rectangle, then apply the output transform.
// All stages are selected by specialization constants, so a new scaler or transform is a new constant value and
// function here plus a pipeline variant on the C# side (OutputTransform / PresentScaler), not a new pass.

// 0 = bilinear. Reserved for later: 1 = bicubic, 2 = Lanczos (manual taps, same inputs).
layout(constant_id = 0) const int SCALER = 0;

// 0 = passthrough (SDR content to an SDR target, PQ content to an HDR10 ST 2084 target; any bit depth).
// 1 = PLACEHOLDER PQ to SDR, only for a display that cannot show HDR10: decode PQ, put 203 nits at SDR white, hard
//     clip, encode sRGB. No gamut mapping, no tone curve (conversions between SDR and HDR are deferred).
layout(constant_id = 1) const int TRANSFORM = 0;

// 1 when the source is PQ (SMPTE ST 2084): scaling filters linear light (decode PQ, weight, encode PQ), so an edge
// between dark and bright keeps its light instead of darkening as it does when PQ code values are averaged. 0: the
// hardware filter on the code values (SDR content).
layout(constant_id = 2) const int LINEAR_PQ = 0;

layout(set = 0, binding = 0) uniform sampler2D uSource;

layout(push_constant) uniform PushConstants
{
    vec2 rectOrigin; // content rectangle in target pixels
    vec2 rectSize;
    int identity;    // 1 when the rectangle is exactly the source size: texel-exact fetch, no filtering
} pc;

layout(location = 0) out vec4 outColor;

const float M1 = 2610.0 / 16384.0;
const float M2 = 2523.0 / 4096.0 * 128.0;
const float C1 = 3424.0 / 4096.0;
const float C2 = 2413.0 / 4096.0 * 32.0;
const float C3 = 2392.0 / 4096.0 * 32.0;

// SMPTE ST 2084 EOTF: code value (0..1) to linear light, 1 = 10,000 nits.
vec3 pqToLinear(vec3 e)
{
    vec3 p = pow(clamp(e, 0.0, 1.0), vec3(1.0 / M2));
    return pow(max(p - C1, 0.0) / (C2 - C3 * p), vec3(1.0 / M1));
}

// Its inverse: linear light (1 = 10,000 nits) to code value.
vec3 linearToPq(vec3 y)
{
    vec3 p = pow(clamp(y, 0.0, 1.0), vec3(M1));
    return pow((C1 + C2 * p) / (1.0 + C3 * p), vec3(M2));
}

vec3 texel(ivec2 p, ivec2 size)
{
    return texelFetch(uSource, clamp(p, ivec2(0), size - 1), 0).rgb;
}

vec3 scaleSample(vec2 local)
{
    if (pc.identity != 0)
    {
        return texelFetch(uSource, ivec2(local), 0).rgb;
    }

    vec2 uv = local / pc.rectSize;
    if (LINEAR_PQ == 0)
    {
        return textureLod(uSource, uv, 0.0).rgb;
    }

    // The hardware filter's footprint and weights (texel centres at i + 0.5, clamp to edge), on linear light.
    ivec2 size = textureSize(uSource, 0);
    vec2 p = uv * vec2(size) - 0.5;
    vec2 f = fract(p);
    ivec2 i = ivec2(floor(p));
    vec3 a = pqToLinear(texel(i, size));
    vec3 b = pqToLinear(texel(i + ivec2(1, 0), size));
    vec3 c = pqToLinear(texel(i + ivec2(0, 1), size));
    vec3 d = pqToLinear(texel(i + ivec2(1, 1), size));
    return linearToPq(mix(mix(a, b, f.x), mix(c, d, f.x), f.y));
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
        return linearToSrgb(clamp(pqToLinear(c) * (10000.0 / 203.0), 0.0, 1.0));
    }

    return c;
}

void main()
{
    vec2 local = gl_FragCoord.xy - pc.rectOrigin;
    outColor = vec4(outputTransform(scaleSample(local)), 1.0);
}
