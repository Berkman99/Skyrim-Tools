#version 330 core
out vec4 FragColor;

// The RGB color for the debug line.
uniform vec3 lineColor;

void main()
{
    // Set the fragment's output color to the specified line color with full alpha.
    FragColor = vec4(lineColor, 1.0);
}