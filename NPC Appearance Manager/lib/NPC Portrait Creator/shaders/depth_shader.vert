// depth_shader.vert

#version 330 core

// === INPUTS (Per-Vertex Data) ===
// Vertex attributes are in the mesh's local Model Space, which uses a Z-up axis convention.
layout (location = 0) in vec3 aPos_modelSpace_zUp;
layout (location = 2) in vec2 aTexCoords; // ADDED: Texture coordinates
layout (location = 4) in ivec4 aBoneIDs;
layout (location = 5) in vec4 aWeights;

// === OUTPUTS (Varyings) ===
out vec2 v_TexCoords; // ADDED: Pass texture coordinates to fragment shader

// === UNIFORMS ===
// Transforms a vertex from the NIF's root space (Z-up) to the light's clip space.
uniform mat4 u_nifRootToLightClip_transform_zUp;
// Transforms a vertex from the mesh's local Model Space (Z-up) to the NIF's root space (Z-up).
uniform mat4 u_modelToNifRoot_transform_zUp;

uniform bool uIsSkinned;
const int MAX_BONES = 80;
// Bone matrices transform a vertex from its bind-pose Model Space (Z-up)
// to its animated-pose Model Space (Z-up).
uniform mat4 uBoneMatrices[MAX_BONES];

void main()
{
    // 1. Start with the original vertex position in Z-up Model Space.
    vec4 pos_modelSpace_zUp = vec4(aPos_modelSpace_zUp, 1.0);

    // 2. If the mesh is skinned, calculate the animated position.
    // The result remains in Z-up Model Space.
    if (uIsSkinned)
    {
        float totalWeight = aWeights.x + aWeights.y + aWeights.z + aWeights.w;
        if (totalWeight > 0.0) {
           mat4 skinMatrix = (aWeights.x * uBoneMatrices[aBoneIDs.x] +
                              aWeights.y * uBoneMatrices[aBoneIDs.y] +
                              aWeights.z * uBoneMatrices[aBoneIDs.z] +
                              aWeights.w * uBoneMatrices[aBoneIDs.w]) / totalWeight;
           pos_modelSpace_zUp = skinMatrix * pos_modelSpace_zUp;
        }
    }
    
    // Pass texture coordinates through
    v_TexCoords = aTexCoords; // ADDED

    // 3. Transform the vertex into the light's clip space to generate the depth map.
    // The calculation is: (NIF Root -> Light Clip) * (Model -> NIF Root) * (Model Pos)
    gl_Position = u_nifRootToLightClip_transform_zUp * u_modelToNifRoot_transform_zUp * pos_modelSpace_zUp;
}