# Run after the game part is built (cmake -DDLL=<the built dll> -P CopyToDev.cmake).
# For development only: when the environment variable NAM_DEV_DLL_FOLDER is set to a folder (a mod's SKSE\Plugins
# folder), the built game part is copied there too. Without it, nothing happens.
if(DEFINED ENV{NAM_DEV_DLL_FOLDER} AND IS_DIRECTORY "$ENV{NAM_DEV_DLL_FOLDER}")
    file(COPY "${DLL}" DESTINATION "$ENV{NAM_DEV_DLL_FOLDER}")
    message(STATUS "Copied the game part to $ENV{NAM_DEV_DLL_FOLDER}")
endif()
