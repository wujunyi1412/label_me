cmake_minimum_required(VERSION 3.20)

project(LabelMeWpf LANGUAGES NONE)

# CMake is the public build entry point. The actual WPF compilation is handled
# by the .NET SDK because CMake has no native C# / WPF compiler pipeline.
find_program(DOTNET_EXECUTABLE NAMES dotnet REQUIRED)

set(DOTNET_CONFIGURATION "Release" CACHE STRING "The .NET build configuration")
set_property(CACHE DOTNET_CONFIGURATION PROPERTY STRINGS Debug Release)

set(PROJECT_FILE "${CMAKE_CURRENT_SOURCE_DIR}/LabelMeWpf.csproj")
set(PUBLISH_DIR "${CMAKE_CURRENT_SOURCE_DIR}/dist/${DOTNET_CONFIGURATION}")
set(DOTNET_BUILD_DIR "${CMAKE_BINARY_DIR}/dotnet-bin/")

add_custom_target(LabelMeWpf ALL
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${PUBLISH_DIR}"
    COMMAND "${DOTNET_EXECUTABLE}" publish "${PROJECT_FILE}"
            --configuration "${DOTNET_CONFIGURATION}"
            --no-self-contained
            --output "${PUBLISH_DIR}"
            "-p:BaseOutputPath=${DOTNET_BUILD_DIR}"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Publishing LabelMeWpf (${DOTNET_CONFIGURATION}) to ${PUBLISH_DIR}"
    VERBATIM
    USES_TERMINAL
)

add_custom_target(run
    COMMAND "${DOTNET_EXECUTABLE}" "${PUBLISH_DIR}/LabelMeWpf.dll"
    DEPENDS LabelMeWpf
    WORKING_DIRECTORY "${PUBLISH_DIR}"
    COMMENT "Starting LabelMeWpf"
    VERBATIM
    USES_TERMINAL
)
