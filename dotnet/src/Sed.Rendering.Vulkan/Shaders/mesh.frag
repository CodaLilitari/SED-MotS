#version 450

layout(set = 0, binding = 0) uniform sampler2D matTex;    // R8 palette index
layout(set = 0, binding = 1) uniform sampler2D palette;   // 256x1 RGBA
layout(set = 0, binding = 2) uniform sampler2D lightRamp; // 256x64 R8

layout(push_constant) uniform Push {
    mat4 mvp;
    vec4 camPos;
    vec4 sky;
    vec4 camAngles;
    vec4 horizScreen;
    vec2 invTexSize;
    uint mode;
    float alpha;
    float brightness;
    uint skyMode;
    uint clampMode;
    uint lightingMode;
} pc;

layout(location = 0) in vec3 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorld;
layout(location = 3) in float vMono;
layout(location = 4) in vec3 vSolidColor;
layout(location = 0) out vec4 outColor;

const float PI = 3.14159265359;

void main()
{
    if (pc.mode == 1u) { outColor = vec4(vColor, 1.0); return; }   // flat: markers/selection
    // Solid palette colors are resolved per vertex, then interpolated like rdCache output.
    if ((pc.lightingMode & 2u) != 0u) { outColor = vec4(vSolidColor, pc.alpha); return; }

    vec2 uv = vUv;
    float light = clamp(vMono, 0.0, 1.0);
    vec3 rgbLight = clamp(vColor, 0.0, 1.0);

    if (pc.skyMode == 1u) {              // ceiling sky: project view ray onto ceiling plane
        vec3 dir = normalize(vWorld - pc.camPos.xyz);
        float t = dir.z != 0.0 ? (pc.sky.x - pc.camPos.z) / dir.z : 0.0;
        vec3 hit = pc.camPos.xyz + dir * max(t, 0.0);
        uv = vec2((hit.x + pc.sky.y) * 16.0, (hit.y + pc.sky.z) * 16.0) * pc.invTexSize;
        light = 1.0;
        rgbLight = vec3(1.0);
    } else if (pc.skyMode == 2u) {      // horizon sky: screen-space, scrolls with view
        vec2 c = gl_FragCoord.xy - pc.horizScreen.zw * 0.5;
        float nx = c.x / pc.horizScreen.z;
        float ny = -c.y / pc.horizScreen.w;
        float roll = radians(pc.camAngles.z);
        float rc = cos(roll), rs = sin(roll);
        uv = vec2(nx * rc - ny * rs + pc.camAngles.x / 360.0 + pc.horizScreen.x,
                  ny * rc + nx * rs + pc.camAngles.y / 360.0 + pc.horizScreen.y);
        light = 1.0;
        rgbLight = vec3(1.0);
    }

    // FF_TexClampX / FF_TexClampY: clamp addressing instead of repeat. Done here
    // rather than with a sampler because addressing is a per-surface property and
    // surfaces sharing a material share one sampler. Half-texel inset keeps the
    // clamped edge off the neighbouring row/column of the palette-index texture.
    if (pc.clampMode != 0u) {
        vec2 half_texel = 0.5 * pc.invTexSize;
        if ((pc.clampMode & 1u) != 0u) uv.x = clamp(uv.x, half_texel.x, 1.0 - half_texel.x);
        if ((pc.clampMode & 2u) != 0u) uv.y = clamp(uv.y, half_texel.y, 1.0 - half_texel.y);
    }

    bool rgbMode = (pc.lightingMode & 1u) != 0u;
    uint lightMode = (pc.lightingMode >> 8u) & 255u;
    int idx = int(texture(matTex, uv).r * 255.0 + 0.5);
    if ((pc.alpha < 0.999 || (pc.lightingMode & 8u) != 0u) && idx == 0) discard;

    vec3 rgb;
    vec2 paletteUv = vec2((float(idx) + 0.5) / 256.0, 0.5);
    vec3 baseRgb = texture(palette, paletteUv).rgb;
    if (lightMode == 0u || pc.skyMode != 0u || pc.brightness >= 1.0) {
        // Fullbright means the source palette entry, even when ramp row63 is not identity.
        rgb = baseRgb;
    } else if (rgbMode && lightMode == 3u) {
        // MotS stores mono and RGB as alternatives. Mono must not multiply RGB.
        rgb = baseRgb * mix(rgbLight, vec3(1.0), pc.brightness);
    } else {
        // Unlit/diffuse faces use monochrome even in RGB display mode.
        float li = mix(light, 1.0, pc.brightness);
        if ((pc.lightingMode & 4u) != 0u) {
            // OpenJK accelerated textured scalar path modulates the base palette.
            // Its CMP-driven emissive/bloom contribution is a separate engine pass.
            rgb = baseRgb * li;
        } else {
            // Explicit CMP comparison profile retained for editor tools.
            int level = int(li * 63.0);
            int shaded = int(texture(lightRamp, vec2((float(idx) + 0.5) / 256.0,
                                                     (float(level) + 0.5) / 64.0)).r * 255.0 + 0.5);
            rgb = texture(palette, vec2((float(shaded) + 0.5) / 256.0, 0.5)).rgb;
        }
    }
    outColor = vec4(rgb, pc.alpha);
}
