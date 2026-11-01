cmake_minimum_required(VERSION 3.21)
project(SedCppBridge VERSION 0.1.0 LANGUAGES CXX)
enable_testing()
find_package(Python3 3.10 REQUIRED COMPONENTS Interpreter)

set(SED_CPP_UPSTREAM_DIR "" CACHE PATH "External exact smlu/SED b822559 source snapshot")
if(NOT IS_ABSOLUTE "${SED_CPP_UPSTREAM_DIR}")
  message(FATAL_ERROR "Pass an absolute verified SED_CPP_UPSTREAM_DIR; configure never fetches source")
endif()
execute_process(
  COMMAND "${Python3_EXECUTABLE}" -B "${CMAKE_CURRENT_SOURCE_DIR}/verify_upstream.py"
    --root "${SED_CPP_UPSTREAM_DIR}" --lock "${CMAKE_CURRENT_SOURCE_DIR}/upstream.lock.json"
    --receipt "${CMAKE_CURRENT_BINARY_DIR}/verified-upstream.json"
  RESULT_VARIABLE _source_verified OUTPUT_VARIABLE _source_receipt ERROR_VARIABLE _source_error
  TIMEOUT 60
)
if(NOT _source_verified EQUAL 0)
  message(FATAL_ERROR "Pinned upstream source verification failed: ${_source_error}")
endif()
file(SHA256 "${CMAKE_CURRENT_SOURCE_DIR}/upstream.lock.json" _source_manifest_sha256)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/upstream.lock.json")

# Recheck every build, including incremental builds; configure-time evidence
# alone does not bind subsequently changed external source.
add_custom_target(verify_upstream_inputs
  COMMAND "${Python3_EXECUTABLE}" -B "${CMAKE_CURRENT_SOURCE_DIR}/verify_upstream.py"
    --root "${SED_CPP_UPSTREAM_DIR}" --lock "${CMAKE_CURRENT_SOURCE_DIR}/upstream.lock.json"
    --receipt "${CMAKE_CURRENT_BINARY_DIR}/verified-upstream.json"
  COMMENT "Verifying exact pinned external upstream source"
  VERBATIM)

set(SED_BUILD_APP OFF CACHE BOOL "" FORCE)
set(SED_BUILD_FUZZERS OFF CACHE BOOL "" FORCE)
set(SED_CPP_STANDARD 20 CACHE STRING "" FORCE)
add_subdirectory("${SED_CPP_UPSTREAM_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/upstream" EXCLUDE_FROM_ALL)
add_dependencies(sed_core verify_upstream_inputs)

add_executable(sed_cpp_bridge src/main.cpp)
set_target_properties(sed_cpp_bridge PROPERTIES OUTPUT_NAME "sed-cpp-bridge")
target_compile_features(sed_cpp_bridge PRIVATE cxx_std_20)
target_link_libraries(sed_cpp_bridge PRIVATE sed_core)
target_compile_definitions(sed_cpp_bridge PRIVATE
  SED_BRIDGE_VERSION="0.1.0"
  SED_BRIDGE_UPSTREAM_COMMIT="b82255998fb733c8e8c87104eb7c17f27b48376f"
  SED_BRIDGE_SOURCE_MANIFEST_SHA256="${_source_manifest_sha256}"
)
if(MSVC)
  target_compile_options(sed_cpp_bridge PRIVATE /W4)
else()
  target_compile_options(sed_cpp_bridge PRIVATE -Wall -Wextra -Wpedantic)
endif()

add_executable(bridge_json_tests tests/json_tests.cpp)
target_compile_features(bridge_json_tests PRIVATE cxx_std_20)
target_include_directories(bridge_json_tests PRIVATE src)

set(_bridge_test_temp "${CMAKE_CURRENT_BINARY_DIR}/test-temp")
add_test(NAME bridge_protocol
  COMMAND "${Python3_EXECUTABLE}" -B "${CMAKE_CURRENT_SOURCE_DIR}/tests/test_worker.py"
    --binary $<TARGET_FILE:sed_cpp_bridge> --upstream "${SED_CPP_UPSTREAM_DIR}"
    --scratch "${_bridge_test_temp}" -v)
add_test(NAME bridge_source_lock
  COMMAND "${Python3_EXECUTABLE}" -B "${CMAKE_CURRENT_SOURCE_DIR}/tests/test_upstream_lock.py"
    --verifier "${CMAKE_CURRENT_SOURCE_DIR}/verify_upstream.py"
    --scratch "${_bridge_test_temp}" -v)
add_test(NAME bridge_json COMMAND bridge_json_tests)
set_tests_properties(bridge_protocol bridge_source_lock bridge_json PROPERTIES TIMEOUT 180 LABELS "bridge")
