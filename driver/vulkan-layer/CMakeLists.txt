cmake_minimum_required(VERSION 3.20)
project(FrGenVulkanLayer VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(VULKAN_HEADERS_DIR "" CACHE PATH "Optional path to Vulkan-Headers/include")
if (VULKAN_HEADERS_DIR)
    set(Vulkan_INCLUDE_DIRS "${VULKAN_HEADERS_DIR}")
else()
    find_package(Vulkan REQUIRED)
endif()

add_library(VK_LAYER_FRGEN SHARED
    src/open_framegen_layer.cpp
)

set_target_properties(VK_LAYER_FRGEN PROPERTIES
    PREFIX ""
    OUTPUT_NAME "VK_LAYER_FRGEN"
)

target_include_directories(VK_LAYER_FRGEN PRIVATE
    ${Vulkan_INCLUDE_DIRS}
)

if (WIN32)
    target_compile_definitions(VK_LAYER_FRGEN PRIVATE
        VK_USE_PLATFORM_WIN32_KHR
        WIN32_LEAN_AND_MEAN
        NOMINMAX
    )
endif()

install(TARGETS VK_LAYER_FRGEN
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .
)
install(FILES FrGen_layer.json DESTINATION .)
