#version 330 core

// === INPUTS (Per-Vertex Data) ===
// All input attributes are in the mesh's local Model Space.
layout (location = 0) in vec3 aPos_modelSpace;
layout (location = 1) in vec3 aNormal_modelSpace;
layout (location = 2) in vec2 aTexCoords;
layout (location = 3) in vec4 aColor;
layout (location = 4) in ivec4 aBoneIDs;
layout (location = 5) in vec4 aWeights;
layout (location = 6) in vec3 aTangent_modelSpace;
layout (location = 7) in vec3 aBitangent_modelSpace;

// === OUTPUTS (Varyings to Fragment Shader) ===
// The fragment's position in camera View Space (Y-up).
out vec3 v_viewSpacePos;
// The texture coordinates, passed through.
out vec2 TexCoords;
// The vertex color, passed through.
out vec4 vertexColor;
// A 3x3 matrix that transforms a normal vector from Tangent Space to View Space.
out mat3 v_tangentToViewMatrix;
// A 3x3 matrix that transforms a normal vector from Model Space to View Space.
out mat3 v_modelToViewNormalMatrix;
// The fragment's position in the shadow-casting light's Clip Space.
out vec4 v_lightClipSpacePos;

// === UNIFORMS ===
// Transforms a vertex from the mesh's local Model Space to the renderer's World Space (Y-up).
uniform mat4 u_model_localToWorld;
// Transforms a vertex from World Space (Y-up) to the camera's View Space (Y-up).
uniform mat4 u_view_worldToView;
// Transforms a vertex from View Space (Y-up) to Clip Space.
uniform mat4 u_proj_viewToClip;
// Transforms a vertex from World Space (Y-up) to the light's Clip Space (for shadow mapping).
uniform mat4 u_worldToLightClip_transform;
// NEW: Per-mesh normal rotation
uniform float u_normalRotationPitch;
uniform float u_normalRotationYaw;

uniform bool u_flipUvs;
uniform vec2 u_uvScale;

uniform bool uIsSkinned;
const int MAX_BONES = 80;
// Bone matrices transform a vertex from its bind-pose Model Space to its animated-pose Model Space.
uniform mat4 uBoneMatrices[MAX_BONES];

void main()
{
    // A temporary variable for the model-space position, which may be modified by skinning.
    vec4 current_pos_modelSpace = vec4(aPos_modelSpace, 1.0);
    // This matrix will be used for transforming normals. It needs to include the skinning transform.
    mat4 final_transform_for_normals = u_model_localToWorld;

    if (uIsSkinned)
    {
        float totalWeight = aWeights.x + aWeights.y + aWeights.z + aWeights.w;
        if (totalWeight > 0.0) {
           mat4 skinMatrix = (aWeights.x * uBoneMatrices[aBoneIDs.x] +
                              aWeights.y * uBoneMatrices[aBoneIDs.y] +
                              aWeights.z * uBoneMatrices[aBoneIDs.z] +
                              aWeights.w * uBoneMatrices[aBoneIDs.w]) / totalWeight;
            
            // Deform the vertex position. The result is still in the mesh's local model space.
            current_pos_modelSpace = skinMatrix * current_pos_modelSpace;
            
            // For correct lighting on a deformed surface, the normal matrix must also account for the skinning.
            final_transform_for_normals = u_model_localToWorld * skinMatrix;
        }
    }
    
    // Now apply the standard model-to-world transformation to the (potentially skinned) vertex.
    // This correctly positions the vertex and converts it from Z-up to Y-up space.
    vec4 pos_worldSpace_yUp = u_model_localToWorld * current_pos_modelSpace;

    // --- Common Calculations for All Vertex Types ---
    
    // Final clip-space position.
    gl_Position = u_proj_viewToClip * u_view_worldToView * pos_worldSpace_yUp;

    // Calculate outputs for the fragment shader.
    v_viewSpacePos = vec3(u_view_worldToView * pos_worldSpace_yUp);
    v_lightClipSpacePos = u_worldToLightClip_transform * pos_worldSpace_yUp;

    // Create the normal and TBN matrices for view space, using the final combined transform.
    mat3 normalMatrix_modelToWorld_yUp = mat3(transpose(inverse(final_transform_for_normals)));
    mat3 normalMatrix_modelToView_yUp = mat3(u_view_worldToView) * normalMatrix_modelToWorld_yUp;
    v_modelToViewNormalMatrix = normalMatrix_modelToView_yUp;
    
    // === NEW: Apply per-mesh normal rotation ===
    // Create rotation matrices for pitch (X-axis) and yaw (Y-axis)
    float pitchRad = radians(u_normalRotationPitch);
    float yawRad = radians(u_normalRotationYaw);
    
    // Pitch rotation (around X-axis)
    mat3 pitchRotation = mat3(
        1.0, 0.0, 0.0,
        0.0, cos(pitchRad), -sin(pitchRad),
        0.0, sin(pitchRad), cos(pitchRad)
    );
    
    // Yaw rotation (around Y-axis)
    mat3 yawRotation = mat3(
        cos(yawRad), 0.0, sin(yawRad),
        0.0, 1.0, 0.0,
        -sin(yawRad), 0.0, cos(yawRad)
    );
    
    // Combine rotations: apply yaw first, then pitch
    mat3 normalRotation = pitchRotation * yawRotation;
    
    // Apply rotation to the normal matrix
    mat3 rotatedNormalMatrix = normalMatrix_modelToView_yUp * normalRotation;
    // === END NEW ===
    
    vec3 T_viewSpace = normalize(rotatedNormalMatrix * aTangent_modelSpace);
    vec3 B_viewSpace = normalize(rotatedNormalMatrix * aBitangent_modelSpace);
    vec3 N_viewSpace = normalize(rotatedNormalMatrix * aNormal_modelSpace);
    v_tangentToViewMatrix = mat3(T_viewSpace, B_viewSpace, N_viewSpace);

    // Pass through other attributes.
    // The C++ application now determines if a flip is needed.
    TexCoords = aTexCoords * u_uvScale; // Apply UV scaling
    if (u_flipUvs) {
        TexCoords.x = 1.0 - aTexCoords.x;
    }
    vertexColor = aColor;
}