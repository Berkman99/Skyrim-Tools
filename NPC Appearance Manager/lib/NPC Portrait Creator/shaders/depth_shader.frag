// depth_shader.frag

#version 330 core

// === INPUTS (Varyings) ===
in vec2 v_TexCoords;

// === UNIFORMS ===
uniform sampler2D texture_diffuse1;
uniform float alpha_threshold;
uniform bool use_alpha_test;

void main()
{
    // For depth-only passes, we don't output color.
    // However, for objects with cutout transparency (like hair),
    // we MUST perform an alpha test to avoid incorrect self-shadowing.

    if (use_alpha_test) {
        // Sample the diffuse texture to get the alpha value.
        vec4 albedo = texture(texture_diffuse1, v_TexCoords);

        // If the alpha is below the threshold, discard the fragment entirely.
        // It will not be written to the depth buffer.
        if (albedo.a < alpha_threshold) {
            discard;
        }
    }
    
    // For opaque fragments, or if alpha testing is disabled, do nothing.
    // The depth value is automatically written.
}