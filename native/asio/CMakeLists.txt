# SPDX-License-Identifier: GPL-3.0-only
cmake_minimum_required(VERSION 3.20)
project(simplehpsdr_asio VERSION 1.0.0 LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "simplehpsdr_asio is Windows-only")
endif()

# Embed a normalized hash of the runtime-affecting source inputs. The repository
# audit compares this marker with the checked-in x64 DLL so source fixes cannot
# silently ship with an older installer artifact.
set(SIMPLEHPSDR_ASIO_HASH_INPUTS
    CMakeLists.txt
    simplehpsdr_asio.h
    simplehpsdr_asio.cpp
    sample_conversion.h
    vendor/asiosdk-2.3.4/common/asio.h
    vendor/asiosdk-2.3.4/common/asiosys.h
    vendor/asiosdk-2.3.4/common/iasiodrv.h)
set(SIMPLEHPSDR_ASIO_HASH_MATERIAL "")
foreach(relative_path IN LISTS SIMPLEHPSDR_ASIO_HASH_INPUTS)
    set(source_path "${CMAKE_CURRENT_SOURCE_DIR}/${relative_path}")
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${source_path}")
    file(READ "${source_path}" source_text)
    string(REPLACE "\r\n" "\n" source_text "${source_text}")
    string(REPLACE "\r" "\n" source_text "${source_text}")
    string(SHA256 source_digest "${source_text}")
    string(APPEND SIMPLEHPSDR_ASIO_HASH_MATERIAL "${relative_path}:${source_digest}\n")
endforeach()
string(SHA256 SIMPLEHPSDR_ASIO_SOURCE_HASH "${SIMPLEHPSDR_ASIO_HASH_MATERIAL}")
file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/generated")
configure_file(source_hash.h.in generated/source_hash.h @ONLY)

add_library(simplehpsdr_asio SHARED simplehpsdr_asio.cpp)
target_compile_features(simplehpsdr_asio PRIVATE cxx_std_17)
target_include_directories(simplehpsdr_asio PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_BINARY_DIR}/generated
    ${CMAKE_CURRENT_SOURCE_DIR}/vendor/asiosdk-2.3.4/common)
target_compile_definitions(simplehpsdr_asio PRIVATE
    WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_link_libraries(simplehpsdr_asio PRIVATE ole32 advapi32 user32)

if(MSVC)
    target_compile_options(simplehpsdr_asio PRIVATE /W4 /wd4324 /permissive- /EHsc /utf-8)
    set_property(TARGET simplehpsdr_asio PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

set_target_properties(simplehpsdr_asio PROPERTIES
    OUTPUT_NAME "simplehpsdr_asio"
    PREFIX "")

include(CTest)
if(BUILD_TESTING)
    add_executable(simplehpsdr_asio_tests tests/simplehpsdr_asio_tests.cpp)
    target_compile_features(simplehpsdr_asio_tests PRIVATE cxx_std_17)
    target_include_directories(simplehpsdr_asio_tests PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_SOURCE_DIR}/vendor/asiosdk-2.3.4/common)
    target_link_libraries(simplehpsdr_asio_tests PRIVATE simplehpsdr_asio)
    if(MSVC)
        target_compile_options(simplehpsdr_asio_tests PRIVATE /W4 /permissive- /EHsc /utf-8)
        set_property(TARGET simplehpsdr_asio_tests PROPERTY
            MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    endif()
    add_test(NAME simplehpsdr_asio_abi_smoke COMMAND simplehpsdr_asio_tests)

    add_executable(simplehpsdr_asio_sample_rate_tests tests/sample_rate_tests.cpp)
    target_compile_features(simplehpsdr_asio_sample_rate_tests PRIVATE cxx_std_17)
    target_include_directories(simplehpsdr_asio_sample_rate_tests PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_BINARY_DIR}/generated
        ${CMAKE_CURRENT_SOURCE_DIR}/vendor/asiosdk-2.3.4/common)
    target_compile_definitions(simplehpsdr_asio_sample_rate_tests PRIVATE
        WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
    target_link_libraries(simplehpsdr_asio_sample_rate_tests PRIVATE ole32 advapi32 user32)
    if(MSVC)
        target_compile_options(simplehpsdr_asio_sample_rate_tests PRIVATE /W4 /wd4324 /permissive- /EHsc /utf-8)
        set_property(TARGET simplehpsdr_asio_sample_rate_tests PROPERTY
            MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    endif()
    add_test(NAME simplehpsdr_asio_sample_rate COMMAND simplehpsdr_asio_sample_rate_tests)
endif()
