cmake_minimum_required(VERSION 3.20)
project(PS5MemoryPeekerWeb C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_executable(PS5MemoryPeekerWeb.elf source/main.c)
target_compile_options(PS5MemoryPeekerWeb.elf PRIVATE -Wall -Werror -g)
target_link_options(PS5MemoryPeekerWeb.elf PRIVATE -nostartfiles)
target_link_libraries(PS5MemoryPeekerWeb.elf PRIVATE "${PS5_PAYLOAD_SDK}/target/lib/crt1.o" SceNotification)
