cmake_minimum_required(VERSION 3.21)
project(WinDeepSearchShellExt LANGUAGES CXX RC)

# The product name and version in the DLL's properties; code signing (SignPath) requires them on every signed file.
set(WDS_VERSION "0.0.0" CACHE STRING "Release version, such as 0.2.0")
string(REPLACE "." "," WDS_VERSION_COMMAS "${WDS_VERSION}")
configure_file(version.rc.in version.rc @ONLY)

add_library(WinDeepSearch.ShellExt SHARED SearchCommand.cpp WinDeepSearch.ShellExt.def ${CMAKE_CURRENT_BINARY_DIR}/version.rc)
target_compile_features(WinDeepSearch.ShellExt PRIVATE cxx_std_20)
target_compile_definitions(WinDeepSearch.ShellExt PRIVATE UNICODE _UNICODE NOMINMAX)
target_compile_options(WinDeepSearch.ShellExt PRIVATE $<$<COMPILE_LANGUAGE:CXX>:/W4 /permissive- /EHsc>)
target_link_libraries(WinDeepSearch.ShellExt PRIVATE runtimeobject shlwapi shell32 ole32)
# Static C runtime: the DLL loads into Windows' menu host, which must not need a Visual C++ runtime install.
set_target_properties(WinDeepSearch.ShellExt PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded")
