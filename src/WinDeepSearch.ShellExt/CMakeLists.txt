cmake_minimum_required(VERSION 3.21)
project(WinDeepSearchShellExt LANGUAGES CXX)

add_library(WinDeepSearch.ShellExt SHARED SearchCommand.cpp WinDeepSearch.ShellExt.def)
target_compile_features(WinDeepSearch.ShellExt PRIVATE cxx_std_20)
target_compile_definitions(WinDeepSearch.ShellExt PRIVATE UNICODE _UNICODE NOMINMAX)
target_compile_options(WinDeepSearch.ShellExt PRIVATE /W4 /permissive- /EHsc)
target_link_libraries(WinDeepSearch.ShellExt PRIVATE runtimeobject shlwapi shell32 ole32)
# Static C runtime: the DLL loads into Windows' menu host, which must not need a Visual C++ runtime install.
set_target_properties(WinDeepSearch.ShellExt PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded")
