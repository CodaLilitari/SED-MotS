#version 450

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec3 inColor;
layout(location = 3) in vec2 inUv;
layout(location = 4) in float inMono;

layout(set = 0, binding = 0) uniform sampler2D matTex;
layout(set = 0, binding = 1) uniform sampler2D palette;
layout(set = 0, binding = 2) uniform sampler2D lightRamp;

layout(push_constant) uniform Push {
    mat4 mvp;          // 0
    vec4 camPos;       // 64
    vec4 sky;          // 80  ceiling: (ceilZ, offX, offY, _)
    vec4 camAngles;    // 96  (yawDeg, pitchDeg, rollDeg, _)
    vec4 horizScreen;  // 112 (horizOffX, horizOffY, width, height)
    vec2 invTexSize;   // 128
    uint mode;         // 136
    float alpha;       // 140
    float brightness;  // 144
    uint skyMode;      // 148
    uint clampMode;    // 152  bit0 = clamp U, bit1 = clamp V
    uint lightingMode; // 156  bit0 RGB, bit1 solid MAT, bits8..15 face light mode
} pc;

layout(location = 0) out vec3 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorld;
layout(location = 3) out float vMono;
layout(location = 4) out vec3 vSolidColor;

vec3 solidColor()
{
    int idx = int(texelFetch(matTex, ivec2(0), 0).r * 255.0 + 0.5);
    vec2 paletteUv = vec2((float(idx) + 0.5) / 256.0, 0.5);
    vec3 baseRgb = textureLod(palette, paletteUv, 0.0).rgb;
    uint lightMode = (pc.lightingMode >> 8u) & 255u;
    if (lightMode == 0u || pc.skyMode != 0u || pc.brightness >= 1.0) return baseRgb;
    if ((pc.lightingMode & 1u) != 0u && lightMode == 3u) {
        int baseline = int(textureLod(lightRamp, vec2(paletteUv.x, 0.5 / 64.0), 0.0).r * 255.0 + 0.5);
        vec3 baselineRgb = textureLod(palette, vec2((float(baseline) + 0.5) / 256.0, 0.5), 0.0).rgb;
        // rdCache converts/clamps each vertex to bytes before interpolation.
        vec3 colorBytes = floor(clamp(clamp(inColor, 0.0, 1.0) * 255.0 - 0.5 + baselineRgb * 255.0, 0.0, 255.0));
        return mix(colorBytes / 255.0, baseRgb, pc.brightness);
    }
    float li = mix(clamp(inMono, 0.0, 1.0), 1.0, pc.brightness);
    int row = int(li * 63.0);
    int shaded = int(textureLod(lightRamp, vec2(paletteUv.x, (float(row) + 0.5) / 64.0), 0.0).r * 255.0 + 0.5);
    return textureLod(palette, vec2((float(shaded) + 0.5) / 256.0, 0.5), 0.0).rgb;
}

void main()
{
    gl_Position = pc.mvp * vec4(inPos, 1.0);
    vColor = inColor;
    vUv = inUv * pc.invTexSize;
    vWorld = inPos;
    vMono = inMono;
    vSolidColor = (pc.lightingMode & 2u) != 0u ? solidColor() : vec3(0.0);
}
