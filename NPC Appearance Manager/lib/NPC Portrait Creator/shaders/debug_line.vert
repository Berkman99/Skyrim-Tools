#version 330 core

// === INPUTS (Per-Vertex Data) ===
// The vertex position of the line, in its own local Model Space.
layout (location = 0) in vec3 aPos_modelSpace;

// === UNIFORMS ===
// Transforms the line's vertices from local Model Space to the renderer's World Space (Y-up).
uniform mat4 u_model_localToWorld_yUp;
// Transforms vertices from World Space (Y-up) to the camera's View Space (Y-up).
uniform mat4 u_view_worldToView_yUp;
// Transforms vertices from View Space (Y-up) to Clip Space.
uniform mat4 u_proj_viewToClip_yUp;

void main()
{
    // Standard Model-View-Projection transformation pipeline.
    gl_Position = u_proj_viewToClip_yUp * u_view_worldToView_yUp * u_model_localToWorld_yUp * vec4(aPos_modelSpace, 1.0);
}