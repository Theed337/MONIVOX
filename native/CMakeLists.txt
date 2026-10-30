cmake_minimum_required(VERSION 3.20)
project(MonivoxNative LANGUAGES CXX)

if(NOT MSVC)
  message(FATAL_ERROR "Monivox native Komponenten benötigen MSVC unter Windows.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_compile_options(/W4 /permissive- /EHsc /utf-8)
add_compile_definitions(UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00)

# Compositor: zeigt virtuelle Monitore auf dem physischen Monitor an
add_executable(MonivoxCompositor WIN32 src/compositor.cpp)
target_link_libraries(MonivoxCompositor PRIVATE d3d11 dxgi d3dcompiler windowsapp)

# Werkzeug zum Anlegen/Entfernen des Software-Geräts für den Treiber
add_executable(MonivoxDevice src/devicetool.cpp)
target_link_libraries(MonivoxDevice PRIVATE onecore)
