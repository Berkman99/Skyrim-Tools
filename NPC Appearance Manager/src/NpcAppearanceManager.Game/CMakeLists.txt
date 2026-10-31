cmake_minimum_required(VERSION 3.25)

# NAM's game part: an SKSE plugin that gives picked NPCs their faces when Skyrim starts.
project(NpcAppearanceManager VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# CommonLibSSE-NG 7.0.0: one build for Skyrim SE, AE and VR through Address Library.
include(FetchContent)
set(BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_SE ON CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_AE ON CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_VR ON CACHE BOOL "" FORCE)
FetchContent_Declare(
    CommonLibSSE
    GIT_REPOSITORY https://github.com/alandtse/CommonLibSSE-NG.git
    GIT_TAG 8b032fa992750d654d6d38a33731714d8b86be1f
    GIT_SUBMODULES extern/openvr
)
FetchContent_MakeAvailable(CommonLibSSE)

find_package(tomlplusplus CONFIG REQUIRED)
find_package(lz4 CONFIG REQUIRED)
find_package(ZLIB REQUIRED)
find_path(DETOURS_INCLUDE_DIRS "detours/detours.h" REQUIRED)
find_library(DETOURS_LIBRARY detours REQUIRED)

add_library(${PROJECT_NAME} SHARED
    src/main.cpp
    src/PCH.h
    src/Files/Archive.cpp
    src/Files/Archive.h
    src/Files/Redirect.cpp
    src/Files/Redirect.h
    src/Picks/Forms.cpp
    src/Picks/Forms.h
    src/Picks/Looks.cpp
    src/Picks/Looks.h
    src/Picks/Picks.cpp
    src/Picks/Picks.h
    src/Picks/Voices.cpp
    src/Picks/Voices.h
)

target_include_directories(${PROJECT_NAME} PRIVATE src ${DETOURS_INCLUDE_DIRS})
target_precompile_headers(${PROJECT_NAME} PRIVATE src/PCH.h)
target_link_libraries(${PROJECT_NAME} PRIVATE
    CommonLibSSE::CommonLibSSE
    tomlplusplus::tomlplusplus
    lz4::lz4
    ZLIB::ZLIB
    ${DETOURS_LIBRARY}
)
target_compile_definitions(${PROJECT_NAME} PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(${PROJECT_NAME} PRIVATE /W4 /permissive- /Zc:preprocessor /utf-8 /EHsc /MP)

# NAM ships the built game part from lib\Game.
add_custom_command(TARGET ${PROJECT_NAME} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:${PROJECT_NAME}>"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../lib/Game/$<TARGET_FILE_NAME:${PROJECT_NAME}>"
)

# Development only: also copies it to the folder in NAM_DEV_DLL_FOLDER, if that's set (see CopyToDev.cmake).
add_custom_command(TARGET ${PROJECT_NAME} POST_BUILD
    COMMAND ${CMAKE_COMMAND} "-DDLL=$<TARGET_FILE:${PROJECT_NAME}>" -P "${CMAKE_CURRENT_SOURCE_DIR}/CopyToDev.cmake"
)
