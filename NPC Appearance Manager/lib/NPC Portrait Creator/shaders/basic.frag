#version 330 core
out vec4 FragColor;

// === INPUTS (Varyings from Vertex Shader) ===
// The fragment's position in camera View Space.
in vec3 v_viewSpacePos;
in vec2 TexCoords;
in vec4 vertexColor;
// Transforms a normal from Tangent Space to View Space.
in mat3 v_tangentToViewMatrix;
// Transforms a normal from Model Space to View Space.
in mat3 v_modelToViewNormalMatrix;
// The fragment's position in the light's Clip Space.
in vec4 v_lightClipSpacePos;

#define MAX_LIGHTS 5

struct Light {
    int type; // 0:disabled, 1:ambient, 2:directional
    // Light direction is pre-transformed into View Space on the CPU.
    vec3 direction;
    vec3 color;
    float intensity; };

// --- TEXTURE SAMPLERS ---
uniform sampler2D texture_diffuse1;
uniform sampler2D texture_normal;
uniform sampler2D texture_skin;
uniform sampler2D texture_detail;
uniform sampler2D texture_specular;
uniform sampler2D texture_face_tint;
uniform sampler2D texture_envmap_2d; // For 2D spherical maps
uniform samplerCube texture_envmap_cube; // For cubemaps
uniform sampler2D texture_envmask;
uniform sampler2D shadowMap;

// --- MATERIAL FLAGS (from NIF file) ---
uniform bool has_normal_map;
uniform bool has_skin_map;
uniform bool has_detail_map;
uniform bool has_specular;
uniform bool has_specular_map;
uniform bool has_face_tint_map;
uniform bool has_greyscale_to_palette;
uniform bool has_environment_map;
uniform bool has_eye_environment_map;
uniform bool is_envmap_cube;
uniform bool has_env_mask; // Environment map mask
uniform bool has_tint_color;
uniform bool has_emissive;
uniform bool is_model_space; // For _msn.dds normal maps

// --- NEW: Special lighting flags ---
uniform bool has_hair_soft_lighting;
uniform bool has_soft_lighting;

// --- NEW: Compatibility Uniforms ---
uniform bool u_suppressSpecularOnVertexColor;
uniform bool has_vertex_colors;


// --- RENDERER TOGGLES (from UI) ---
uniform bool use_alpha_test;
uniform bool u_useDiffuseMap;
uniform bool u_useNormalMap;
uniform bool u_useSkinMap;
uniform bool u_useDetailMap;
uniform bool u_useSpecularMap;
uniform bool u_useFaceTintMap;
uniform bool u_useEnvironmentMap;
uniform bool u_useEmissive;

// --- NEW: Texture Debug View Toggles ---
uniform bool u_debug_diffuse;
uniform bool u_debug_normal;
uniform bool u_debug_skin;
uniform bool u_debug_detail;
uniform bool u_debug_specular;
uniform bool u_debug_faceTint;
uniform bool u_debug_environmentMask;

// --- MATERIAL PROPERTIES ---
uniform float alpha_threshold;
uniform float envMapScale;
uniform float eyeCubemapScale; // NEW: Separate scale for eyes
uniform float greyscaleToPaletteScale;
uniform vec3 tint_color;
uniform vec3 emissiveColor;
uniform float emissiveMultiple;
uniform float materialGlossiness;
uniform float materialSpecularStrength;

// --- NEW: Advanced Lighting Uniforms ---
uniform float rimlightPower;
uniform float subsurfaceRolloff;

// --- EYE SHADER PROPERTIES ---
uniform bool is_eye;
uniform float eye_fresnel_strength;
uniform float eye_spec_power;

// --- GENERAL UNIFORMS ---
// Transforms from World Space to View Space. Used to get reflection vectors back into world space for cubemapping.
uniform mat4 u_view_worldToView;
// The camera's position in World Space. NOTE: Not used in current view-space lighting model.
uniform vec3 u_cameraPos_worldSpace_yUp;
uniform Light lights[MAX_LIGHTS];
uniform vec3 u_backlightColor;

// Calculates the shadow contribution (1.0 = lit, 0.0 = in shadow).
float calculateShadow(vec4 pos_lightClipSpace)
{
    // Perspective divide to get Normalized Device Coordinates (NDC) from the light's perspective.
    vec3 pos_lightNDC = pos_lightClipSpace.xyz / pos_lightClipSpace.w;
    // Transform from [-1,1] range to [0,1] range to use as texture coordinates.
pos_lightNDC = pos_lightNDC * 0.5 + 0.5;
    // If the fragment is outside the shadow map's frustum, it's considered lit.
if (pos_lightNDC.z > 1.0) {
        return 1.0; }

    // Get the closest depth stored in the shadow map at this position.
    float closestDepth = texture(shadowMap, pos_lightNDC.xy).r;
    // Get the current fragment's depth from the light's perspective.
float currentDepth = pos_lightNDC.z;
    // Add a small bias to prevent "shadow acne" self-shadowing artifacts.
float bias = 0.005;
    float shadow = currentDepth - bias > closestDepth ? 0.0 : 1.0;

    return shadow; }


void main()
{
// --- NEW: Texture Debug Views ---
    // If any debug view is active, this section will sample the
    // corresponding texture, output its raw color, and exit the shader
    // early, bypassing all lighting calculations.
    if (u_debug_diffuse) {
        if (u_useDiffuseMap) { FragColor = texture(texture_diffuse1, TexCoords); }
        else { FragColor = vec4(0.5, 0.5, 0.5, 1.0); } // Grey if no texture
        return;
    }
    if (u_debug_normal) {
        if (has_normal_map) { FragColor = vec4(texture(texture_normal, TexCoords).rgb, 1.0); }
        else { FragColor = vec4(0.5, 0.5, 1.0, 1.0); } // Default normal color if no texture
        return;
    }
    if (u_debug_skin) {
        if (has_skin_map) {
            // Skin map is single-channel, so visualize it as greyscale.
            float skinVal = texture(texture_skin, TexCoords).r;
            FragColor = vec4(skinVal, skinVal, skinVal, 1.0);
        }
        else { FragColor = vec4(0.0, 0.0, 0.0, 1.0); } // Black if no texture
        return;
    }
    if (u_debug_detail) {
        if (has_detail_map) { FragColor = vec4(texture(texture_detail, TexCoords).rgb, 1.0); }
        else { FragColor = vec4(0.5, 0.5, 0.5, 1.0); } // Grey if no texture
        return;
    }
    if (u_debug_specular) {
        if (has_specular_map) {
            // Specular map is single-channel, so visualize it as greyscale.
            float specVal = texture(texture_specular, TexCoords).r;
            FragColor = vec4(specVal, specVal, specVal, 1.0);
        }
        else { FragColor = vec4(0.0, 0.0, 0.0, 1.0); } // Black if no texture
        return;
    }
    if (u_debug_faceTint) {
        if (has_face_tint_map) { FragColor = texture(texture_face_tint, TexCoords); }
        else { FragColor = vec4(0.0, 0.0, 0.0, 1.0); } // Black if no texture
        return;
    }
    if (u_debug_environmentMask) {
        if (has_env_mask) {
            // Env mask is single-channel, so visualize it as greyscale.
            float maskVal = texture(texture_envmask, TexCoords).r;
            FragColor = vec4(maskVal, maskVal, maskVal, 1.0);
        }
        else { FragColor = vec4(0.0, 0.0, 0.0, 1.0); } // Black if no texture
        return;
    }

    // --- 1. BASE COLOR & ALPHA TEST ---
    vec4 baseColor = vec4(1.0);
if (u_useDiffuseMap) {
        baseColor = texture(texture_diffuse1, TexCoords); }
    baseColor.rgb *= vertexColor.rgb;
    baseColor.a *= vertexColor.a;
if (use_alpha_test && baseColor.a < alpha_threshold) {
        discard; }

    // Implement Greyscale-to-Palette logic
    if (has_greyscale_to_palette) {
        // This path is for assets like hair that use a greyscale texture for masking a tint color.
        // We use the red channel of the texture as an intensity mask, multiply it by the tint color,
        // and then scale it by the material's 'greyscaleToPaletteScale' property.
        baseColor.rgb = baseColor.rrr * tint_color * greyscaleToPaletteScale;
    } else if (has_tint_color) {
        // This is the fallback for simpler tinting (like for skin), which just multiplies the existing color.
        baseColor.rgb *= tint_color; 
    }

    if (has_face_tint_map && u_useFaceTintMap) {
        vec4 tintSample = texture(texture_face_tint, TexCoords);
        // Overlay tint onto DIFFUSE only: dark mask darkens color; bright mask brightens color.
        // Equivalent to mix(base, base * tint, mask).
        baseColor.rgb = mix(baseColor.rgb, baseColor.rgb * tintSample.rgb, tintSample.a);
    }

    /// DEBUG
    /*
    if (has_normal_map && u_useNormalMap) {
    vec3 normalMapRaw = texture(texture_normal, TexCoords).rgb;
    // Visualize the raw normal map texture
    FragColor = vec4(normalMapRaw, 1.0);
    return; // Exit early to only show the normal map
}*/
/*
    if (has_normal_map && u_useNormalMap) {
    // Calculate which mip level would be sampled
    vec2 dx = dFdx(TexCoords * 512.0); // 512 = texture resolution
    vec2 dy = dFdy(TexCoords * 512.0);
    float delta_max_sqr = max(dot(dx, dx), dot(dy, dy));
    float mipLevel = 0.5 * log2(delta_max_sqr);
    
    // Visualize mip level as a color (0=red, higher=green)
    FragColor = vec4(mipLevel / 9.0, 1.0 - (mipLevel / 9.0), 0.0, 1.0);
    return;
}*/
    // END DEBUG    

    // --- 2. NORMAL CALCULATION ---
    vec3 normal_viewSpace;

    // The tangent vector is the first column of the TBN matrix from the vertex shader.
    // If its length is zero, it means the tangent/bitangent data was missing and the matrix is invalid.
    bool tbnIsValid = length(v_tangentToViewMatrix[0]) > 0.0;

    if (has_normal_map && u_useNormalMap) {
        if (is_model_space) {
            // --- MODEL-SPACE PATH (for Skin) ---
            vec3 normal_modelSpace = texture(texture_normal, TexCoords).rgb * 2.0 - 1.0;

            // PATCH: Invert the green channel for DirectX normal maps.
            // After unpacking to [-1, 1], a vector component is inverted by negating it (*= -1.0).
            // The previous method (1.0 - g) is only correct for the [0, 1] color range.
            normal_modelSpace.g *= -1.0;
            
            normal_viewSpace = normalize(v_modelToViewNormalMatrix * normal_modelSpace);
        }
        else if (tbnIsValid) {
            // --- TANGENT-SPACE PATH (for Hair, with valid data) ---
            vec3 normal_tangentSpace = texture(texture_normal, TexCoords).rgb * 2.0 - 1.0;
            
            // Apply the same patch here for tangent-space normals.
            normal_tangentSpace.g *= -1.0;
            
            normal_viewSpace = normalize(v_tangentToViewMatrix * normal_tangentSpace);
        }
        else {
            // --- FALLBACK PATH (for Eyes/Beard with invalid data) ---
            // The TBN matrix is invalid, so we cannot use the normal map.
            // We fall back to the basic vertex normal (the 3rd column of the matrix).
            normal_viewSpace = normalize(v_tangentToViewMatrix[2]);
        }
    } else {
        // --- NO NORMAL MAP PATH ---
        // No normal map is present, so just use the basic vertex normal.
        normal_viewSpace = normalize(v_tangentToViewMatrix[2]);
    }

    // For eye meshes, normals are often inverted in the NIF. Flip them back.
    if (is_eye) {
        normal_viewSpace = -normal_viewSpace;
    }
    
    // --- 3. DYNAMIC LIGHTING ---
    vec3 finalColor = vec3(0.0);
    float shadow = calculateShadow(v_lightClipSpacePos);
    
    // The lighting calculations are done per-light.
    for (int i = 0; i < MAX_LIGHTS; i++) {
        if (lights[i].type == 0) continue;
        vec3 lightColor = lights[i].color * lights[i].intensity;
        if (lights[i].type == 1) { // Ambient Light
            finalColor += lightColor * baseColor.rgb;
        }
        else if (lights[i].type == 2) { // Directional Light
            vec3 lightDir_viewSpace = normalize(lights[i].direction);
            vec3 viewDir_viewSpace = normalize(-v_viewSpacePos);
            
            // --- MODIFIED: Diffuse Calculation (with Soft Lighting) ---
            float NdotL = dot(normal_viewSpace, lightDir_viewSpace);
            float diffuseStrength;
            if (has_soft_lighting) {
                // If the Soft Lighting flag is set, wrap the light around the surface more.
                // This is done by remapping the dot product from its standard [-1,1] range to a [0,1] range,
                // which creates a much softer light-to-shadow transition.
                diffuseStrength = NdotL * 0.5 + 0.5;
            } else {
                // Standard Blinn-Phong diffuse, clamped at 0 so surfaces facing away from the light are not lit.
                diffuseStrength = max(NdotL, 0.0);
            }
            vec3 diffuse = diffuseStrength * lightColor;

            vec3 specular = vec3(0.0);
            if (has_specular && u_useSpecularMap) {
                float specularStrength = 1.0;
                if (has_specular_map) {
                    // The specular map's red channel acts as a per-pixel mask or intensity scalar.
                    specularStrength = texture(texture_specular, TexCoords).r;
                }
            
                // In View Space, the view direction is simply the vector from the fragment position to the origin.
                vec3 viewDir_viewSpace = normalize(-v_viewSpacePos);
                vec3 halfwayDir_viewSpace = normalize(lightDir_viewSpace + viewDir_viewSpace);

                // MODIFIED: Use the 'materialGlossiness' uniform from the NIF instead of a hard-coded exponent.
                // This value controls the shininess or tightness of the highlight.
                float specAmount = pow(max(dot(normal_viewSpace, halfwayDir_viewSpace), 0.0), materialGlossiness);
                
                // MODIFIED: Modulate the final specular color by the material's overall specular strength.
                specular = specAmount * specularStrength * lightColor * materialSpecularStrength;

                // --- NEW: Apply Specular Suppression ---
                // If the compatibility toggle is enabled and this mesh uses vertex colors,
                // zero out the calculated specular highlight. This emulates fixed-function
                // behavior where specular was disabled to prevent washing out vertex colors.
                if (u_suppressSpecularOnVertexColor && has_vertex_colors) {
                    specular = vec3(0.0);
                }
            }

            // --- NEW: Backlight / Rimlight Calculation (for Hair) ---
            vec3 backlight = vec3(0.0);
            if (has_hair_soft_lighting) {
                // MODIFIED: The tightness of the rim effect is now controlled by the material's `rimLightPower`
                // property, rather than a hardcoded exponent. This allows for fine artistic control.
                float rim = pow(1.0 - max(dot(viewDir_viewSpace, normal_viewSpace), 0.0), rimlightPower);
                backlight = rim * u_backlightColor * lightColor * diffuseStrength * baseColor.rgb;
            }

            // --- NEW: Subsurface Scattering (for Skin) ---
            vec3 subsurface = vec3(0.0);
            if (has_skin_map && u_useSkinMap) {
                // This is a more advanced subsurface scattering (SSS) approximation. It simulates light
                // penetrating a surface (like skin), scattering internally, and exiting, which creates
                // a characteristic soft, "glowing" look.
                
                float sss_mask = texture(texture_skin, TexCoords).r;
                vec3 sss_color = vec3(1.0, 0.3, 0.2); 
                float wrap = (dot(normal_viewSpace, lightDir_viewSpace) * 0.5 + 0.5);

                // CORRECTED: The final SSS contribution is scaled by the light color, the wrap factor,
                // the SSS tint, the texture mask, and the material's `subsurfaceRolloff` property from the NIF.
                subsurface = lights[i].color * wrap * sss_color * sss_mask * subsurfaceRolloff;
            }
            
            // --- Final Composition ---
            // Combine all lighting components. Each is modulated by the shadow factor and the base color.
            finalColor += (diffuse * shadow + specular * shadow + subsurface * shadow + backlight * shadow) * baseColor.rgb;
        }
    }
    
    // --- 4. POST-LIGHTING EFFECTS ---
    vec3 subsurfaceColor = vec3(0.0);
if (has_skin_map && u_useSkinMap) {
        subsurfaceColor = texture(texture_skin, TexCoords).r * vec3(1.0, 0.3, 0.2); // Reddish tint
finalColor += subsurfaceColor * baseColor.rgb;
    }
    
    if (has_detail_map && u_useDetailMap) {
        vec3 detailColor = texture(texture_detail, TexCoords).rgb;
        // Mix the detail map over the existing color.
finalColor = mix(finalColor, detailColor, 0.3);
    }

     // --- 5. ENVIRONMENT MAPPING / REFLECTIONS (OVERHAULED) ---
    if (u_useEnvironmentMap) {
        if (has_eye_environment_map && is_envmap_cube) {
            // --- Eye-Specific Cubemap Reflection ---
            // This path uses a simpler, additive reflection model with its own unique scale factor.
            vec3 viewDir_viewSpace = normalize(-v_viewSpacePos);
            vec3 reflectDir_viewSpace = reflect(-viewDir_viewSpace, normal_viewSpace);
            
            // Transform reflection vector from View Space back to World Space for cubemap sampling.
            vec3 reflectDir_worldSpace = inverse(mat3(u_view_worldToView)) * reflectDir_viewSpace; 
            vec3 envColor = texture(texture_envmap_cube, reflectDir_worldSpace).rgb;
            // Add the reflection using the dedicated eye cubemap scale.
            finalColor += envColor * eyeCubemapScale; 
        } else if (has_environment_map) {
            // --- Regular Environment Mapping (2D or Cube) ---
            vec3 viewDir_viewSpace = normalize(-v_viewSpacePos); 
            vec3 reflectDir_viewSpace = reflect(-viewDir_viewSpace, normal_viewSpace);
            vec3 envColor;

            if (is_envmap_cube) {
                // For cubemaps, sample using the 3D reflection vector in world space.
                vec3 reflectDir_worldSpace = inverse(mat3(u_view_worldToView)) * reflectDir_viewSpace; 
                envColor = texture(texture_envmap_cube, reflectDir_worldSpace).rgb; 
            } else { 
                // For 2D spherical maps, calculate 2D texture coordinates from the reflection vector.
                vec2 envCoords = normalize(reflectDir_viewSpace.xy) * 0.5 + 0.5; 
                envColor = texture(texture_envmap_2d, envCoords).rgb; 
            }

            // --- NEW/CORRECTED LOGIC: Environment Reflection Strength ---
            // To better match NifSkope and the original game engine, we use a specific fallback order
            // for determining the strength of the environment reflection.
            float reflectionStrength = 1.0; // Default to full strength if no mask is found.
            
            // 1. Primary Check: A dedicated environment mask texture from slot 5.
            if (has_env_mask) {
                // If a mask texture is present, its red channel determines the reflection intensity.
                // This gives artists the most direct control over reflections.
                reflectionStrength = texture(texture_envmask, TexCoords).r;
            } 
            // 2. Fallback Check: The specular map from slot 7.
            else if (has_specular_map) {
                // If no dedicated mask exists, fall back to using the specular map's red channel.
                // This is a very common pattern in Skyrim/Fallout materials where one texture
                // controls both specular highlights and environmental reflections.
                reflectionStrength = texture(texture_specular, TexCoords).r;
            }
            
            // Add the final reflection to the color, scaling it by the determined mask value 
            // and the material's overall envMapScale property from the NIF.
            finalColor += envColor * reflectionStrength * envMapScale;
        }
    }

    // --- 6. EMISSIVE COLOR ---
    // This is added last as it's independent of lighting and shadows.
if (has_emissive && u_useEmissive) {
        finalColor += emissiveColor * emissiveMultiple; }

    // --- 7. FINAL OUTPUT ---
    FragColor = vec4(finalColor, baseColor.a);
}