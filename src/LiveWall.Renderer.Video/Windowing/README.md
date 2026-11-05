# Windowing

创建视频 Renderer 子 HWND 和 D3D11 渲染上下文。窗口生命周期从 Initialize 到 Shutdown，禁止跨会话复用裸句柄。

C1.1 的 MpvVideoBackend 在专属 STA render 线程创建、泵送、调整和销毁自有 child，串行拥有 D3D11 immediate context、持久 Render Context 与每 binding 的 DComp target/交换链。parent 必须属于当前 Pipe server PID；不接收或查找 Shell HWND。child 为 disabled/noactivate/toolwindow/no-redirection，不能进入 Alt+Tab 或获取默认输入。

新 binding 先创建全新 child/target，再释放旧目标；保留 mpv core/context 和暂停意图，禁止复用旧提交证明。提交前校验实际 child client 尺寸。Bounds 先变换既有帧并 Commit，避免重建交换链暴露背景；正式替换仍独立准备首帧。停止拒绝新任务，所有排队工作均获明确结束；render 线程释放 context、target/GPU/窗口，控制侧最后销毁 core/library。清理异常不能发送正常 ShutdownCompleted。

